using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Ultiviewer.App.ViewModels;

namespace Ultiviewer.App.Views;

/// <summary>Find-in-notebooks panel: live query box, scope, and grouped results with highlighted matches.</summary>
public partial class SearchPanel : UserControl
{
    public SearchPanel()
    {
        InitializeComponent();
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    public void FocusQuery()
    {
        Dispatcher.BeginInvoke(() =>
        {
            QueryBox.Focus();
            if (QueryBox.Template?.FindName("PART_EditableTextBox", QueryBox) is TextBox box)
            {
                box.Focus();
                box.SelectAll();
            }
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                QueryBox.IsDropDownOpen = false;
                Vm.RunSearch();
                e.Handled = true;
                break;
            case Key.Escape when !QueryBox.IsDropDownOpen:
                Vm.CloseSearchCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Down when !QueryBox.IsDropDownOpen && Vm.SearchResults.Count > 0:
                Results.Focus();
                if (Results.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem first)
                {
                    first.IsSelected = true;
                    first.Focus();
                }

                e.Handled = true;
                break;
        }
    }

    private void OnResultsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Open(Results.SelectedItem, focusEditor: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            FocusQuery();
            e.Handled = true;
        }
    }

    private void OnResultsMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && NotebookTree.FindAncestor<System.Windows.Controls.Primitives.ToggleButton>(source) is null
            && NotebookTree.FindAncestor<TreeViewItem>(source) is { } item)
        {
            Open(item.DataContext, focusEditor: false);
        }
    }

    private void Open(object? item, bool focusEditor)
    {
        switch (item)
        {
            case SearchLineViewModel line:
                Vm.OpenSearchLine(line, focusEditor);
                break;
            case SearchPageViewModel page:
                Vm.OpenSearchPage(page);
                break;
        }
    }
}
