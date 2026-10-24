using System.Windows;
using System.Windows.Input;
using Ultiviewer.Core.Search;

namespace Ultiviewer.App.Views;

/// <summary>
/// A command the palette can run: a menu item or a command that has no menu item. <see cref="Key"/> identifies it
/// across runs (for "recently used").
/// </summary>
public sealed record PaletteItem(string Title, string Location, string Gesture, bool? IsChecked, Action Run)
{
    public string Key => Location.Length == 0 ? Title : Location + " › " + Title;

    public string Separator => Location.Length == 0 ? "" : " › ";

    /// <summary>A check mark for menu options that are on.</summary>
    public string CheckGlyph => IsChecked == true ? "" : "";
}

/// <summary>Ctrl+Shift+P: find and run any command by name; recently used commands come first.</summary>
public partial class CommandPaletteWindow : Window
{
    private readonly IReadOnlyList<PaletteItem> _commands;
    private readonly List<string> _recent;
    private bool _closing;

    public CommandPaletteWindow(IReadOnlyList<PaletteItem> commands, List<string> recent, Window owner)
    {
        InitializeComponent();
        _commands = commands;
        _recent = recent;
        Owner = owner;
        var bounds = owner.WindowState == WindowState.Maximized ? new Rect(0, 0, SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height) : new Rect(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight);
        Left = bounds.Left + (bounds.Width - Width) / 2;
        Top = bounds.Top + 70;
        Loaded += (_, _) =>
        {
            Refresh();
            if (ShowActivated)
            {
                Query.Focus();
            }
        };
    }

    /// <summary>The commands shown for the current query, best first.</summary>
    public IReadOnlyList<PaletteItem> Shown => (IReadOnlyList<PaletteItem>?)Items.ItemsSource ?? [];

    /// <summary>Sets the query as if typed (also used by the snapshot tool).</summary>
    public void Filter(string query)
    {
        Query.Text = query;
        Refresh();
    }

    private void OnQueryChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        var shown = CommandRanking.Rank(_commands, Query.Text, _recent, c => c.Key, c => c.Title, c => c.Location);
        Items.ItemsSource = shown;
        Empty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (shown.Count > 0)
        {
            Items.SelectedIndex = 0;
            Items.ScrollIntoView(Items.SelectedItem);
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                CloseOnce();
                e.Handled = true;
                break;
            case Key.Down:
                Move(+1);
                e.Handled = true;
                break;
            case Key.Up:
                Move(-1);
                e.Handled = true;
                break;
            case Key.PageDown:
                Move(+8);
                e.Handled = true;
                break;
            case Key.PageUp:
                Move(-8);
                e.Handled = true;
                break;
            case Key.Enter:
                RunSelected();
                e.Handled = true;
                break;
        }
    }

    private void Move(int step)
    {
        if (Items.Items.Count == 0)
        {
            return;
        }

        Items.SelectedIndex = Math.Clamp(Items.SelectedIndex + step, 0, Items.Items.Count - 1);
        Items.ScrollIntoView(Items.SelectedItem);
    }

    private void OnItemsMouseUp(object sender, MouseButtonEventArgs e) => RunSelected();

    /// <summary>Closes the palette, then runs the command once focus is back in the main window.</summary>
    public void RunSelected()
    {
        if (Items.SelectedItem is not PaletteItem item)
        {
            return;
        }

        CloseOnce();
        CommandRanking.Remember(_recent, item.Key);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, item.Run);
    }

    private void OnDeactivated(object? sender, EventArgs e) => CloseOnce();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
    }

    private void CloseOnce()
    {
        if (!_closing)
        {
            _closing = true;
            Close();
        }
    }
}
