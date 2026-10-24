using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Ultiviewer.App.ViewModels;
using Ultiviewer.Core.Notebooks;

namespace Ultiviewer.App.Views;

/// <summary>Notebook tree: selection, inline rename, context menus and drag-and-drop.</summary>
public partial class NotebookTree : UserControl
{
    private Point _dragStart;
    private TreeNodeViewModel? _dragCandidate;
    private DropIndicator? _indicator;

    public NotebookTree()
    {
        InitializeComponent();
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    /// <summary>Raised when the user asks to open the selected item (Enter or double-click).</summary>
    public event Action<TreeNodeViewModel>? OpenRequested;

    public void FocusTree()
    {
        if (Tree.ItemContainerGenerator.ContainerFromItem(Vm.SelectedNode) is TreeViewItem item)
        {
            item.Focus();
        }
        else
        {
            Tree.Focus();
        }
    }

    private void OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.OldValue is TreeNodeViewModel { IsEditing: true } previous)
        {
            Vm.CommitRename(previous, cancel: false);
        }

        Vm.SelectedNode = e.NewValue as TreeNodeViewModel;
    }

    /// <summary>An item being renamed hands keyboard focus on to its text box.</summary>
    private void OnItemGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ReferenceEquals(e.NewFocus, sender) && sender is TreeViewItem { DataContext: TreeNodeViewModel { IsEditing: true } } item)
        {
            FocusEditorLater(item);
        }
    }

    private void OnItemSelected(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, sender) && sender is TreeViewItem item)
        {
            item.BringIntoView();
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox || Vm.SelectedNode is not { } node)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.F2:
                Vm.RenameCommand.Execute(node);
                e.Handled = true;
                break;
            case Key.Delete:
                Vm.DeleteCommand.Execute(node);
                e.Handled = true;
                break;
            case Key.Enter:
                OpenRequested?.Invoke(node);
                e.Handled = true;
                break;
        }
    }

    private void OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindAncestor<TextBox>(source) is null
            && FindAncestor<TreeViewItem>(source)?.DataContext is TreeNodeViewModel { Kind: NodeKind.Page or NodeKind.File } node)
        {
            OpenRequested?.Invoke(node);
            e.Handled = true;
        }
    }

    // ---- Inline rename ----

    private void OnEditVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } box)
        {
            FocusEditorLater(box);
        }
    }

    /// <summary>A freshly created item can start out in rename mode, so its box is already visible.</summary>
    private void OnEditLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: TreeNodeViewModel { IsEditing: true } } box)
        {
            FocusEditorLater(box);
        }
    }

    /// <summary>
    /// Focuses the rename box once layout and selection have settled; selecting a new item moves focus to its
    /// TreeViewItem, and a new item's template may not exist yet.
    /// </summary>
    private void FocusEditorLater(FrameworkElement scope) =>
        Dispatcher.BeginInvoke(() =>
        {
            var box = scope as TextBox ?? FindDescendant<TextBox>(scope);
            if (box is { IsVisible: true, DataContext: TreeNodeViewModel { IsEditing: true } } && !box.IsKeyboardFocused)
            {
                box.Focus();
                box.SelectAll();
            }
        }, System.Windows.Threading.DispatcherPriority.ContextIdle);

    private void OnEditKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: TreeNodeViewModel node })
        {
            return;
        }

        if (e.Key is Key.Enter or Key.Escape)
        {
            e.Handled = true;
            Vm.CommitRename(node, cancel: e.Key == Key.Escape);
            FocusTree();
        }
    }

    private void OnEditLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: TreeNodeViewModel node })
        {
            Vm.CommitRename(node, cancel: false);
        }
    }

    // ---- Context menu ----

    private void OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindAncestor<TreeViewItem>(source) is { } item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = Tree.ContextMenu!;
        menu.Items.Clear();
        var vm = Vm;
        var node = vm.SelectedNode;
        if (node is null)
        {
            menu.Items.Add(Item("New notebook…", vm.NewNotebookCommand));
            menu.Items.Add(Item("Open folder as notebook…", vm.OpenNotebookFolderCommand));
            return;
        }

        switch (node.Kind)
        {
            case NodeKind.Notebook:
                menu.Items.Add(Item("New page", vm.NewPageCommand, node, "Ctrl+N"));
                menu.Items.Add(Item("New section", vm.NewSectionCommand, node, "Ctrl+Shift+N"));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Rename", vm.RenameCommand, node, "F2"));
                menu.Items.Add(ColorMenu(node));
                menu.Items.Add(Item("Refresh", vm.RefreshCommand));
                menu.Items.Add(Item("Export as zip…", vm.ExportZipCommand, node));
                menu.Items.Add(Item("Export as HTML or PDF…", vm.ExportDocumentCommand, node));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Open in Explorer", vm.RevealInExplorerCommand, node));
                menu.Items.Add(Item("Copy path", vm.CopyPathCommand, node));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Close notebook", vm.CloseNotebookCommand, node));
                break;

            case NodeKind.Section:
                menu.Items.Add(Item("New page", vm.NewPageCommand, node, "Ctrl+N"));
                menu.Items.Add(Item("New section", vm.NewSectionCommand, node, "Ctrl+Shift+N"));
                menu.Items.Add(Item("New section inside", vm.NewSubsectionCommand, node));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Rename", vm.RenameCommand, node, "F2"));
                menu.Items.Add(Item("Delete", vm.DeleteCommand, node, "Del"));
                menu.Items.Add(ColorMenu(node));
                menu.Items.Add(Item("Export as zip…", vm.ExportZipCommand, node));
                menu.Items.Add(Item("Export as HTML or PDF…", vm.ExportDocumentCommand, node));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Open in Explorer", vm.RevealInExplorerCommand, node));
                menu.Items.Add(Item("Copy path", vm.CopyPathCommand, node));
                break;

            case NodeKind.Page:
                menu.Items.Add(Item("Open", new RelayAction(() => OpenRequested?.Invoke(node)), gesture: "Enter"));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("New page", vm.NewPageCommand, node, "Ctrl+N"));
                menu.Items.Add(Item("New subpage", vm.NewSubpageCommand, node));
                menu.Items.Add(Item("New section", vm.NewSectionCommand, node, "Ctrl+Shift+N"));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Rename", vm.RenameCommand, node, "F2"));
                menu.Items.Add(Item("Delete", vm.DeleteCommand, node, "Del"));
                menu.Items.Add(Item("History…", new RelayAction(() => vm.ShowHistoryFor(node.Path))));
                menu.Items.Add(Item("Export as HTML or PDF…", vm.ExportDocumentCommand, node));
                AddPageExtras(menu, node);
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Open in Explorer", vm.RevealInExplorerCommand, node));
                menu.Items.Add(Item("Copy path", vm.CopyPathCommand, node));
                break;

            default:
                menu.Items.Add(Item("Open", new RelayAction(() => OpenRequested?.Invoke(node)), gesture: "Enter"));
                menu.Items.Add(Item("Open with default app", new RelayAction(() => Services.ShellService.Open(node.Path))));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Rename", vm.RenameCommand, node, "F2"));
                menu.Items.Add(Item("Delete", vm.DeleteCommand, node, "Del"));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Open in Explorer", vm.RevealInExplorerCommand, node));
                menu.Items.Add(Item("Copy path", vm.CopyPathCommand, node));
                break;
        }
    }

    /// <summary>Extra page commands contributed by later features (favorites, link updates…).</summary>
    partial void AddPageExtras(ContextMenu menu, TreeNodeViewModel node);

    private MenuItem ColorMenu(TreeNodeViewModel node)
    {
        var colors = new MenuItem { Header = "Color" };
        foreach (var (name, hex) in MainViewModel.SectionColors)
        {
            var item = new MenuItem
            {
                Header = name,
                IsCheckable = true,
                IsChecked = string.Equals(node.Color, hex, StringComparison.OrdinalIgnoreCase),
                Icon = hex is null ? null : new Rectangle
                {
                    Width = 12,
                    Height = 12,
                    RadiusX = 2,
                    RadiusY = 2,
                    Fill = (Brush)new BrushConverter().ConvertFromString(hex)!,
                },
            };
            item.Click += (_, _) => Vm.SetColor(node, hex);
            colors.Items.Add(item);
        }

        return colors;
    }

    private static MenuItem Item(string header, ICommand command, object? parameter = null, string? gesture = null) =>
        new() { Header = header, Command = command, CommandParameter = parameter, InputGestureText = gesture ?? "" };

    // ---- Drag and drop ----

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = null;
        if (e.OriginalSource is DependencyObject source
            && FindAncestor<TextBox>(source) is null
            && FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(source) is null
            && FindAncestor<TreeViewItem>(source) is { DataContext: TreeNodeViewModel node })
        {
            _dragStart = e.GetPosition(Tree);
            _dragCandidate = node;
        }
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is null || e.LeftButton != MouseButtonState.Pressed || _dragCandidate.IsEditing)
        {
            return;
        }

        var delta = e.GetPosition(Tree) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var node = _dragCandidate;
        _dragCandidate = null;
        DragDrop.DoDragDrop(Tree, new DataObject(typeof(TreeNodeViewModel), node), DragDropEffects.Move);
        ClearIndicator();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var drop = HitDropTarget(e);
        if (drop is null)
        {
            e.Effects = DragDropEffects.None;
            ClearIndicator();
            return;
        }

        e.Effects = e.Data.GetDataPresent(typeof(TreeNodeViewModel)) ? DragDropEffects.Move : DragDropEffects.Copy;
        ShowIndicator(drop.Value.Header, drop.Value.Position);
        AutoScroll(e);
    }

    private void OnDragLeave(object sender, DragEventArgs e) => ClearIndicator();

    private void OnDrop(object sender, DragEventArgs e)
    {
        ClearIndicator();
        if (HitDropTarget(e) is not { } drop)
        {
            return;
        }

        e.Handled = true;
        if (e.Data.GetData(typeof(TreeNodeViewModel)) is TreeNodeViewModel node)
        {
            Vm.MoveNode(node, drop.Target, drop.Position);
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            Vm.ImportFiles(files, drop.Target, drop.Position);
        }
    }

    private (TreeNodeViewModel Target, DropPosition Position, FrameworkElement Header)? HitDropTarget(DragEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || FindAncestor<TreeViewItem>(source) is not { DataContext: TreeNodeViewModel target } item)
        {
            return null;
        }

        var header = item.Template?.FindName("PART_Header", item) as FrameworkElement ?? item;
        var y = e.GetPosition(header).Y;
        var height = Math.Max(1, header.ActualHeight);

        if (e.Data.GetData(typeof(TreeNodeViewModel)) is TreeNodeViewModel dragged)
        {
            if (dragged.DescendantsAndSelf().Contains(target))
            {
                return null;
            }

            if (dragged.Kind == NodeKind.Notebook)
            {
                return target.Kind == NodeKind.Notebook
                    ? (target, y < height / 2 ? DropPosition.Before : DropPosition.After, header)
                    : null;
            }
        }
        else if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return null;
        }

        var position = target.Kind switch
        {
            NodeKind.Notebook => DropPosition.Inside,
            NodeKind.File => y < height / 2 ? DropPosition.Before : DropPosition.After,
            _ => y < height * 0.25 ? DropPosition.Before : y > height * 0.75 ? DropPosition.After : DropPosition.Inside,
        };
        return (target, position, header);
    }

    private void AutoScroll(DragEventArgs e)
    {
        if (FindDescendant<ScrollViewer>(Tree) is not { } scroll)
        {
            return;
        }

        var y = e.GetPosition(Tree).Y;
        if (y < 24)
        {
            scroll.LineUp();
        }
        else if (y > Tree.ActualHeight - 24)
        {
            scroll.LineDown();
        }
    }

    private void ShowIndicator(FrameworkElement header, DropPosition position)
    {
        if (_indicator is not null && ReferenceEquals(_indicator.AdornedElement, header))
        {
            _indicator.Position = position;
            return;
        }

        ClearIndicator();
        if (AdornerLayer.GetAdornerLayer(header) is { } layer)
        {
            _indicator = new DropIndicator(header) { Position = position };
            layer.Add(_indicator);
        }
    }

    private void ClearIndicator()
    {
        if (_indicator is not null)
        {
            AdornerLayer.GetAdornerLayer(_indicator.AdornedElement)?.Remove(_indicator);
            _indicator = null;
        }
    }

    internal static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null and not T)
        {
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return element as T;
    }

    internal static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>Line above/below or a frame around the drop target's header.</summary>
    private sealed class DropIndicator(UIElement adorned) : Adorner(adorned)
    {
        private DropPosition _position;

        public DropPosition Position
        {
            get => _position;
            set
            {
                _position = value;
                InvalidateVisual();
            }
        }

        protected override void OnRender(DrawingContext context)
        {
            IsHitTestVisible = false;
            var size = AdornedElement.RenderSize;
            var pen = new Pen(SystemColors.HighlightBrush, 2);
            switch (Position)
            {
                case DropPosition.Before:
                    context.DrawLine(pen, new Point(0, 1), new Point(size.Width, 1));
                    break;
                case DropPosition.After:
                    context.DrawLine(pen, new Point(0, size.Height - 1), new Point(size.Width, size.Height - 1));
                    break;
                default:
                    context.DrawRoundedRectangle(null, pen, new Rect(new Point(1, 1), new Size(Math.Max(0, size.Width - 2), Math.Max(0, size.Height - 2))), 3, 3);
                    break;
            }
        }
    }
}

/// <summary>Minimal ICommand around an action, for menu items built in code.</summary>
public sealed class RelayAction(Action action) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => action();
}
