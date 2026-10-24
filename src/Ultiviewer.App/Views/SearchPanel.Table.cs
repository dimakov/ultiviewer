using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Ultiviewer.App.ViewModels;
using Ultiviewer.Core.Search;

namespace Ultiviewer.App.Views;

/// <summary>
/// The results table: one row per page with its location, the property columns chosen by the view model,
/// modified time and match count. Clicking a header sorts by it (again: reverses); empty values stay last.
/// </summary>
public partial class SearchPanel
{
    private ListCollectionView? _tableItems;
    private MainViewModel? _tableVm;
    private string? _sortKey;
    private bool _sortDescending;

    private void InitializeTable()
    {
        DataContextChanged += (_, _) => AttachTable();
        ResultsTable.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(OnHeaderClick));
    }

    /// <summary>The rows as shown (sorted), for the snapshot tool.</summary>
    internal IEnumerable<SearchPageViewModel> TableRows => _tableItems?.Cast<SearchPageViewModel>() ?? [];

    private void AttachTable()
    {
        if (DataContext is not MainViewModel vm || vm == _tableVm)
        {
            return;
        }

        _tableVm = vm;
        _tableItems = new ListCollectionView(vm.SearchResults);
        ResultsTable.ItemsSource = _tableItems;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.TableColumns))
            {
                BuildColumns();
            }
        };
        BuildColumns();
    }

    private void BuildColumns()
    {
        if (_tableVm is not { } vm)
        {
            return;
        }

        TableView.Columns.Clear();
        AddColumn("Page", "title", nameof(SearchPageViewModel.Title), 220);
        AddColumn("Location", "location", nameof(SearchPageViewModel.Location), 170);
        foreach (var key in vm.TableColumns)
        {
            AddColumn(key, "prop:" + key, $"{nameof(SearchPageViewModel.Properties)}[{key}]", 120);
        }

        AddColumn("Modified", "modified", nameof(SearchPageViewModel.ModifiedLabel), 140);
        AddColumn("Matches", "matches", nameof(SearchPageViewModel.MatchCount), 70);
        Sort(); // a sort by a property the new results lack keeps them in title order until it is back

    }

    private void AddColumn(string title, string key, string path, double width) =>
        TableView.Columns.Add(new GridViewColumn
        {
            Header = new GridViewColumnHeader { Content = title, Tag = key, HorizontalContentAlignment = HorizontalAlignment.Left },
            DisplayMemberBinding = new Binding(path) { Mode = BindingMode.OneWay },
            Width = width,
        });

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader { Tag: string key })
        {
            SortBy(key);
        }
    }

    /// <summary>Sorts by a column key ("title", "location", "modified", "matches" or "prop:&lt;key&gt;"); the same key again reverses.</summary>
    internal void SortBy(string key)
    {
        _sortDescending = _sortKey == key && !_sortDescending;
        _sortKey = key;
        Sort();
    }

    private void Sort()
    {
        foreach (var header in TableView.Columns.Select(c => c.Header).OfType<GridViewColumnHeader>())
        {
            var title = ((string)header.Content).TrimEnd(' ', '▲', '▼');
            header.Content = (string)header.Tag == _sortKey ? title + (_sortDescending ? " ▼" : " ▲") : title;
        }

        if (_tableItems is not null)
        {
            _tableItems.CustomSort = _sortKey is null ? null : new ResultComparer(_sortKey, _sortDescending);
        }
    }

    private void OnTableMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && NotebookTree.FindAncestor<ListViewItem>(source) is { DataContext: SearchPageViewModel page })
        {
            Vm.OpenSearchPage(page);
        }
    }

    private void OnTableKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ResultsTable.SelectedItem is SearchPageViewModel page)
        {
            Vm.OpenSearchPage(page);
            Vm.FocusEditor();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            FocusQuery();
            e.Handled = true;
        }
    }

    private sealed class ResultComparer(string key, bool descending) : System.Collections.IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not SearchPageViewModel a || y is not SearchPageViewModel b)
            {
                return 0;
            }

            if (key.StartsWith("prop:", StringComparison.Ordinal))
            {
                var property = key[5..];
                var (left, right) = (a.Properties[property], b.Properties[property]);
                if (left.Length == 0 || right.Length == 0)
                {
                    return PropertyValues.CompareForSort(left, right) is var empty && empty != 0 ? empty : Title(a, b); // empty last either way
                }

                return Directed(PropertyValues.Compare(left, right), a, b);
            }

            return Directed(key switch
            {
                "title" => Title(a, b),
                "location" => string.Compare(a.Location, b.Location, StringComparison.CurrentCultureIgnoreCase),
                "modified" => a.Modified.CompareTo(b.Modified),
                "matches" => a.MatchCount.CompareTo(b.MatchCount),
                _ => 0,
            }, a, b);
        }

        private int Directed(int result, SearchPageViewModel a, SearchPageViewModel b) =>
            result == 0 ? Title(a, b) : descending ? -result : result;

        private static int Title(SearchPageViewModel a, SearchPageViewModel b) =>
            string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);
    }
}
