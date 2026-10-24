using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Ultiviewer.App.ViewModels;

namespace Ultiviewer.App.Views;

/// <summary>The active page's headings; clicking one (or Enter) moves the caret there.</summary>
public partial class OutlinePane : UserControl
{
    public OutlinePane()
    {
        InitializeComponent();
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private void OnItemClicked(object sender, MouseButtonEventArgs e)
    {
        // Clicks on the expander arrow only expand or collapse.
        if (e.OriginalSource is DependencyObject source && NotebookTree.FindAncestor<System.Windows.Controls.Primitives.ToggleButton>(source) is null
            && NotebookTree.FindAncestor<TreeViewItem>(source)?.DataContext is OutlineItem item)
        {
            Vm.GoToOutlineItem(item);
        }
    }

    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Tree.SelectedItem is OutlineItem item)
        {
            Vm.GoToOutlineItem(item);
            e.Handled = true;
        }
    }
}
