using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Ultiviewer.App.ViewModels;

namespace Ultiviewer.App.Views;

/// <summary>Backlinks, the page's own links and unlinked mentions of the active page.</summary>
public partial class LinksPane : UserControl
{
    public LinksPane()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && DataContext is MainViewModel vm)
            {
                vm.RefreshLinks();
            }
        };
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private void OnRowClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && NotebookTree.FindAncestor<Button>(source) is null
            && NotebookTree.FindAncestor<ListBoxItem>(source)?.DataContext is LinkEntry entry)
        {
            Vm.OpenLinkEntry(entry);
        }
    }

    private void OnRowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (sender as ListBox)?.SelectedItem is LinkEntry entry)
        {
            Vm.OpenLinkEntry(entry);
            e.Handled = true;
        }
    }

    private void OnLinkMention(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LinkEntry mention)
        {
            Vm.LinkMention(mention);
        }
    }

    /// <summary>The lists sit in one scroll viewer; the wheel scrolls it, not the (non-scrolling) list.</summary>
    private void OnInnerWheel(object sender, MouseWheelEventArgs e)
    {
        if (Content is ScrollViewer outer)
        {
            outer.ScrollToVerticalOffset(outer.VerticalOffset - e.Delta / 3.0);
            e.Handled = true;
        }
    }
}
