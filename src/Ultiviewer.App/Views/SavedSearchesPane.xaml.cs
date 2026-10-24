using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Ultiviewer.App.ViewModels;
using Ultiviewer.Core.Settings;

namespace Ultiviewer.App.Views;

/// <summary>Saved searches above the notebook tree: click (or Enter) runs one; right-click to rename, reorder or delete.</summary>
public partial class SavedSearchesPane : UserControl
{
    public SavedSearchesPane()
    {
        InitializeComponent();
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private void OnClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && NotebookTree.FindAncestor<ListBoxItem>(source) is { DataContext: SavedSearch saved })
        {
            Vm.RunSavedSearchCommand.Execute(saved);
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (List.SelectedItem is not SavedSearch saved)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                Vm.RunSavedSearchCommand.Execute(saved);
                e.Handled = true;
                break;
            case Key.F2:
                Vm.RenameSavedSearchCommand.Execute(saved);
                e.Handled = true;
                break;
            case Key.Delete:
                Vm.DeleteSavedSearchCommand.Execute(saved);
                e.Handled = true;
                break;
        }
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = List.ContextMenu!;
        menu.Items.Clear();
        if (e.OriginalSource is not DependencyObject source || NotebookTree.FindAncestor<ListBoxItem>(source) is not { DataContext: SavedSearch saved } item)
        {
            e.Handled = true;
            return;
        }

        item.IsSelected = true;
        menu.Items.Add(new MenuItem { Header = "_Run", Command = Vm.RunSavedSearchCommand, CommandParameter = saved });
        menu.Items.Add(new MenuItem { Header = "Re_name…", Command = Vm.RenameSavedSearchCommand, CommandParameter = saved, InputGestureText = "F2" });
        menu.Items.Add(new MenuItem { Header = "Move _up", Command = Vm.MoveSavedSearchUpCommand, CommandParameter = saved, IsEnabled = Vm.SavedSearches.IndexOf(saved) > 0 });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "_Delete", Command = Vm.DeleteSavedSearchCommand, CommandParameter = saved, InputGestureText = "Del" });
    }
}
