using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Ultiviewer.App.Services;
using Ultiviewer.App.Views;

namespace Ultiviewer.App;

/// <summary>The command palette (built from the menus) and shortcut changes made in Settings.</summary>
public partial class MainWindow
{
    private void ShowCommandPalette() => new CommandPaletteWindow(PaletteCommands(), _vm.Session.RecentCommands, this).Show();

    /// <summary>
    /// Every command that can run now: each menu item (titled by its menu path, the dynamic Go and template
    /// menus included), then application and editor commands that have no menu item.
    /// </summary>
    internal IReadOnlyList<PaletteItem> PaletteCommands()
    {
        BuildGoMenu();
        BuildTemplatesMenu();
        var target = ActiveEditorHost.TextEditor.TextArea;
        var items = new List<PaletteItem>();
        var seen = new HashSet<ICommand>();
        foreach (var menu in MainMenu.Items.OfType<MenuItem>())
        {
            Harvest(menu, [], items, seen, target);
        }

        foreach (var (description, command, gesture) in KeyboardShortcuts.Commands(_vm, _vm.Settings.Shortcuts))
        {
            if (seen.Add(command) && command != _vm.ShowCommandPaletteCommand && CanRun(command, null, target))
            {
                items.Add(new PaletteItem(description, "", gesture, null, () => RunCommand(command, null, target)));
            }
        }

        return items;
    }

    private void Harvest(MenuItem item, List<string> path, List<PaletteItem> items, HashSet<ICommand> seen, IInputElement target)
    {
        var title = MenuText(item.Header);
        if (item.Visibility != Visibility.Visible || title.Length == 0)
        {
            return;
        }

        if (item.Items.OfType<MenuItem>().Any())
        {
            foreach (var child in item.Items.OfType<MenuItem>().ToList())
            {
                Harvest(child, [.. path, title], items, seen, target);
            }

            return;
        }

        var location = string.Join(" › ", path);
        var isChecked = item.IsCheckable ? item.IsChecked : (bool?)null;
        if (item.Command is { } command)
        {
            seen.Add(command);
            if (command == _vm.ShowCommandPaletteCommand)
            {
                return;
            }

            var parameter = item.CommandParameter;
            if (CanRun(command, parameter, target))
            {
                items.Add(new PaletteItem(title, location, item.InputGestureText, isChecked, () => RunCommand(command, parameter, target)));
            }
        }
        else if (item.IsEnabled)
        {
            // Options bound to a setting (View › Word wrap) toggle; other items run their Click handlers.
            Action run = item.IsCheckable
                ? () => item.IsChecked = !item.IsChecked
                : () => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
            items.Add(new PaletteItem(title, location, item.InputGestureText, isChecked, run));
        }
    }

    /// <summary>Editor commands go to the focused editor, wherever keyboard focus is.</summary>
    private static bool CanRun(ICommand command, object? parameter, IInputElement target) =>
        command is RoutedCommand routed ? routed.CanExecute(parameter, target) : command.CanExecute(parameter);

    private static void RunCommand(ICommand command, object? parameter, IInputElement target)
    {
        if (command is RoutedCommand routed)
        {
            routed.Execute(parameter, target);
        }
        else
        {
            command.Execute(parameter);
        }
    }

    /// <summary>A menu header as plain text: access-key underscores removed ("Save _as…" → "Save as…").</summary>
    private static string MenuText(object? header) => header is string text
        ? text.Replace("__", "\0").Replace("_", "").Replace("\0", "_")
        : "";

    /// <summary>Applies the shortcuts saved in Settings to the editor commands, the window and the menus.</summary>
    internal void ApplyShortcuts()
    {
        KeyboardShortcuts.ApplyEditorOverrides(_vm.Settings.Shortcuts);
        KeyboardShortcuts.BindApplicationCommands(this, _vm, _vm.Settings.Shortcuts);
        RefreshMenuGestures(MainMenu);
    }

    /// <summary>Shows each menu item's current shortcut (they can change in Settings).</summary>
    private void RefreshMenuGestures(ItemsControl parent)
    {
        foreach (var item in parent.Items.OfType<MenuItem>())
        {
            // An item with a parameter (Help › Keyboard shortcuts) isn't what the command's shortcut does.
            if (item is { Command: { } command, CommandParameter: null } && KeyboardShortcuts.GestureText(command, _vm, _vm.Settings.Shortcuts) is { } text)
            {
                item.InputGestureText = text;
            }

            RefreshMenuGestures(item);
        }
    }
}
