using System.Windows;
using System.Windows.Input;
using Ultiviewer.App.Editor;
using Ultiviewer.App.ViewModels;

namespace Ultiviewer.App.Services;

/// <summary>A command with its default and effective keyboard shortcut, for binding and the settings list.</summary>
public sealed record ShortcutEntry(string Name, string Description, string DefaultGesture, string Gesture);

/// <summary>
/// All keyboard shortcuts. Defaults live here; <c>settings.json → shortcuts</c> overrides them by command name
/// (e.g. <c>"DuplicateLine": "Ctrl+Alt+D"</c>, or <c>""</c> to remove a shortcut).
/// </summary>
public static class KeyboardShortcuts
{
    private static readonly KeyGestureConverter Converter = new();

    /// <summary>Application commands: name, description, default gesture(s) separated by '|', command.</summary>
    private static IEnumerable<(string Name, string Description, string Gesture, Func<MainViewModel, ICommand> Command)> AppCommands()
    {
        yield return ("NewPage", "New page", "Ctrl+N", vm => vm.NewPageCommand);
        yield return ("NewSection", "New section", "Ctrl+Shift+N", vm => vm.NewSectionCommand);
        yield return ("Refresh", "Refresh notebooks", "F5", vm => vm.RefreshCommand);
        yield return ("NewFile", "New file", "Ctrl+T", vm => vm.NewFileCommand);
        yield return ("OpenFile", "Open file", "Ctrl+O", vm => vm.OpenFileCommand);
        yield return ("Save", "Save", "Ctrl+S", vm => vm.SaveCommand);
        yield return ("SaveAs", "Save as", "Ctrl+Alt+S", vm => vm.SaveAsCommand);
        yield return ("SaveAll", "Save all", "Ctrl+Shift+S", vm => vm.SaveAllCommand);
        yield return ("CloseTab", "Close tab", "Ctrl+W|Ctrl+F4", vm => vm.CloseCommand);
        yield return ("ReopenClosedTab", "Reopen closed tab", "Ctrl+Shift+T", vm => vm.ReopenClosedCommand);
        yield return ("NextTab", "Next tab", "Ctrl+Tab|Ctrl+PageDown", vm => vm.NextTabCommand);
        yield return ("PreviousTab", "Previous tab", "Ctrl+Shift+Tab|Ctrl+PageUp", vm => vm.PreviousTabCommand);
        yield return ("ZoomIn", "Zoom in", "Ctrl+OemPlus|Ctrl+Add", vm => vm.ZoomInCommand);
        yield return ("ZoomOut", "Zoom out", "Ctrl+OemMinus|Ctrl+Subtract", vm => vm.ZoomOutCommand);
        yield return ("ZoomReset", "Reset zoom", "Ctrl+D0|Ctrl+NumPad0", vm => vm.ZoomResetCommand);
        yield return ("CycleViewMode", "Cycle Live / Edit / Split / Preview", "Ctrl+Shift+V", vm => vm.CycleViewModeCommand);
        yield return ("GoToPage", "Go to page", "Ctrl+E", vm => vm.QuickOpenCommand);
        yield return ("PageHistory", "Page history", "Ctrl+Shift+H", vm => vm.ShowHistoryCommand);
        yield return ("TodaysNote", "Today's daily note", "Ctrl+Shift+D", vm => vm.OpenTodayCommand);
        yield return ("SplitEditor", "Two editors side by side", "Ctrl+Oem5", vm => vm.SplitRightCommand);
        yield return ("FocusOtherEditor", "Switch to the other editor", "Ctrl+Shift+Oem5", vm => vm.FocusOtherGroupCommand);
        yield return ("Settings", "Settings", "Ctrl+OemComma", vm => vm.OpenSettingsCommand);
    }

    /// <summary>Applies overrides to the editor commands' gestures (call once, before the editor is created).</summary>
    public static void ApplyEditorOverrides(IReadOnlyDictionary<string, string> overrides)
    {
        _ = EditorDefaults.Count; // capture the defaults before anything is overridden
        foreach (var command in AllEditorCommands())
        {
            if (overrides.TryGetValue(command.Name, out var text))
            {
                command.InputGestures.Clear();
                foreach (var gesture in Parse(text))
                {
                    command.InputGestures.Add(gesture);
                }
            }
        }
    }

    /// <summary>Creates the window's key bindings for application commands, honouring overrides.</summary>
    public static void BindApplicationCommands(Window window, MainViewModel vm, IReadOnlyDictionary<string, string> overrides)
    {
        foreach (var (name, _, defaults, command) in AppCommands())
        {
            var text = overrides.TryGetValue(name, out var custom) ? custom : defaults;
            foreach (var gesture in Parse(text))
            {
                window.InputBindings.Add(new KeyBinding(command(vm), gesture));
            }
        }
    }

    /// <summary>Every shortcut for the settings window.</summary>
    public static IReadOnlyList<ShortcutEntry> All(IReadOnlyDictionary<string, string> overrides)
    {
        var list = AppCommands()
            .Select(c => new ShortcutEntry(c.Name, c.Description, Pretty(c.Gesture), Pretty(overrides.TryGetValue(c.Name, out var o) ? o : c.Gesture)))
            .ToList();
        list.Add(new ShortcutEntry("FindInNotebooks", "Find in notebooks", "Ctrl+Shift+F", "Ctrl+Shift+F"));
        list.AddRange(AllEditorCommands().Select(c => new ShortcutEntry(
            c.Name, c.Text, Pretty(string.Join("|", DefaultEditorGesture(c))), Pretty(string.Join("|", c.InputGestures.OfType<KeyGesture>().Select(Format))))));
        return list;
    }

    /// <summary>The built-in editor shortcuts, whatever the overrides (AvalonEdit's clashing keys stay removed).</summary>
    public static IReadOnlyList<KeyGesture> DefaultEditorGestures() =>
        EditorDefaults.Values.SelectMany(g => g).SelectMany(Parse).ToList();

    private static readonly Dictionary<string, string[]> EditorDefaults = AllEditorCommands()
        .ToDictionary(c => c.Name, c => c.InputGestures.OfType<KeyGesture>().Select(Format).ToArray());

    private static IEnumerable<string> DefaultEditorGesture(RoutedUICommand command) =>
        EditorDefaults.TryGetValue(command.Name, out var gestures) ? gestures : [];

    private static IEnumerable<RoutedUICommand> AllEditorCommands() => typeof(EditorCommands)
        .GetProperties()
        .Where(p => p.PropertyType == typeof(RoutedUICommand))
        .Select(p => (RoutedUICommand)p.GetValue(null)!);

    private static IEnumerable<KeyGesture> Parse(string text)
    {
        foreach (var part in text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            KeyGesture? gesture = null;
            try
            {
                gesture = Converter.ConvertFromInvariantString(part) as KeyGesture;
            }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException or FormatException)
            {
                ErrorLog.Write(ex, $"Ignoring invalid shortcut \"{part}\"");
            }

            if (gesture is not null)
            {
                yield return gesture;
            }
        }
    }

    private static string Format(KeyGesture gesture) => Converter.ConvertToInvariantString(gesture) ?? "";

    private static string Pretty(string text) => text
        .Replace("OemPlus", "+").Replace("OemMinus", "-").Replace("OemComma", ",").Replace("D0", "0").Replace("D1", "1")
        .Replace("|", ", ");
}
