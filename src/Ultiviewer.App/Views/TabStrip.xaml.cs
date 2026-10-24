using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Ultiviewer.App.ViewModels;

namespace Ultiviewer.App.Views;

/// <summary>
/// Notepad++-style tab strip for one editor group (its DataContext): click to switch, middle-click or ✕ to close,
/// drag to reorder or onto the other editor's strip to move the tab there.
/// </summary>
public partial class TabStrip : UserControl
{
    private Point _dragStart;
    private DocumentTabViewModel? _dragCandidate;

    public TabStrip()
    {
        InitializeComponent();
    }

    /// <summary>Set by the pane; the strip's DataContext is its <see cref="EditorGroup"/>.</summary>
    public MainViewModel Vm { get; set; } = null!;

    private EditorGroup Group => (EditorGroup)DataContext;

    private static DocumentTabViewModel? TabAt(object source) =>
        source is DependencyObject element ? NotebookTree.FindAncestor<ListBoxItem>(element)?.DataContext as DocumentTabViewModel : null;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (List.SelectedItem is { } item)
        {
            List.ScrollIntoView(item);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DocumentTabViewModel tab)
        {
            Vm.CloseTab(tab, Group);
        }
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var tab = TabAt(e.OriginalSource);
        _dragCandidate = null;
        Vm.FocusedGroup = Group; // commands from this strip (and its context menu) act on this editor
        if (tab is null)
        {
            return;
        }

        if (e.ChangedButton == MouseButton.Middle)
        {
            Vm.CloseTab(tab, Group);
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Left && NotebookTree.FindAncestor<Button>(e.OriginalSource as DependencyObject) is null)
        {
            _dragStart = e.GetPosition(List);
            _dragCandidate = tab;
            Group.ActiveTab = tab;
            Dispatcher.BeginInvoke(Vm.FocusEditor, System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TabAt(e.OriginalSource) is { } tab)
        {
            tab.IsPreviewTab = false;
        }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (NotebookTree.FindDescendant<ScrollViewer>(List) is { } scroll)
        {
            scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta / 2.0);
            e.Handled = true;
        }
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var delta = e.GetPosition(List) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance * 2)
        {
            return;
        }

        var tab = _dragCandidate;
        _dragCandidate = null;
        DragDrop.DoDragDrop(List, new DataObject(typeof(DocumentTabViewModel), tab), DragDropEffects.Move);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(DocumentTabViewModel)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(DocumentTabViewModel)) is DocumentTabViewModel dragged)
        {
            var index = TabAt(e.OriginalSource) is { } target ? Group.Tabs.IndexOf(target) : Group.Tabs.Count;
            Vm.MoveTab(Group, dragged, index);
            Vm.FocusedGroup = Group;
            Group.ActiveTab = dragged;
        }
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = List.ContextMenu!;
        menu.Items.Clear();
        var vm = Vm;
        var tab = TabAt(e.OriginalSource) ?? vm.ActiveTab;
        if (tab is null)
        {
            e.Handled = true;
            return;
        }

        menu.Items.Add(Item("Close", vm.CloseCommand, tab, "Ctrl+W"));
        menu.Items.Add(Item("Close others", vm.CloseOthersCommand, tab));
        menu.Items.Add(Item("Close tabs to the right", vm.CloseToRightCommand, tab));
        menu.Items.Add(Item("Close all", vm.CloseAllCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(vm.IsSplit ? "Open in the other editor too" : "Open in a second editor", vm.OpenInOtherGroupCommand, tab, vm.IsSplit ? null : "Ctrl+\\"));
        menu.Items.Add(Item(vm.IsSplit ? "Move to the other editor" : "Move to a second editor", vm.MoveToOtherGroupCommand, tab));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(tab.IsPinned ? "Unpin" : "Pin", vm.TogglePinCommand, tab));
        if (tab.IsPreviewTab)
        {
            menu.Items.Add(Item("Keep open", vm.KeepTabOpenCommand, tab));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Save", vm.SaveCommand, tab, "Ctrl+S"));
        menu.Items.Add(Item("Save as…", vm.SaveAsCommand, tab, "Ctrl+Alt+S"));
        if (tab.Path is not null)
        {
            menu.Items.Add(Item("Reload from disk", vm.ReloadCommand, tab));
            menu.Items.Add(Item("History…", vm.ShowHistoryCommand, tab, "Ctrl+Shift+H"));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Show in notebook tree", vm.RevealTabInTreeCommand, tab));
            menu.Items.Add(Item("Open containing folder", vm.OpenTabFolderCommand, tab));
            menu.Items.Add(Item("Copy path", vm.CopyTabPathCommand, tab));
        }
    }

    private static MenuItem Item(string header, ICommand command, object? parameter = null, string? gesture = null) =>
        new() { Header = header, Command = command, CommandParameter = parameter, InputGestureText = gesture ?? "" };
}
