using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Ultiviewer.App.Editor;
using Ultiviewer.App.ViewModels;

namespace Ultiviewer.App.Services;

/// <summary>
/// A command's shortcut in the settings window: <see cref="Gesture"/> in KeyGesture syntax ("Ctrl+Shift+P",
/// alternatives separated by '|'), editable unless fixed. <see cref="Conflict"/> names other commands with the same
/// keys; <see cref="Note"/> names those that share them by design (Markdown-only formatting vs. the rest).
/// </summary>
public sealed partial class ShortcutRow(string name, string description, string defaultGesture, string gesture, bool editable = true, bool markdownOnly = false) : ObservableObject
{
    public string Name { get; } = name;

    public string Description { get; } = description;

    public string DefaultGesture { get; } = defaultGesture;

    public bool IsEditable { get; } = editable;

    /// <summary>The command only runs in Markdown pages, where its keys win over other commands' (Ctrl+U underlines there).</summary>
    public bool MarkdownOnly { get; } = markdownOnly;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    private string? _note;

    public bool HasNote => Note is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Display), nameof(IsChanged))]
    private string _gesture = gesture;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConflict))]
    private string? _conflict;

    public string Display => KeyboardShortcuts.Pretty(Gesture);

    public bool IsChanged => Gesture != DefaultGesture;

    public bool HasConflict => Conflict is not null;
}

/// <summary>
/// All keyboard shortcuts. Defaults live here; <c>settings.json → shortcuts</c> overrides them by command name
/// (e.g. <c>"DuplicateLine": "Ctrl+Alt+D"</c>, or <c>""</c> to remove a shortcut).
/// </summary>
public static partial class KeyboardShortcuts
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
        yield return ("ExportDocument", "Export as HTML or PDF", "Ctrl+Shift+E", vm => vm.ExportDocumentCommand);
        yield return ("Print", "Print", "Ctrl+P", vm => vm.PrintCommand);
        yield return ("SplitEditor", "Two editors side by side", "Ctrl+Oem5", vm => vm.SplitRightCommand);
        yield return ("FocusOtherEditor", "Switch to the other editor", "Ctrl+Shift+Oem5", vm => vm.FocusOtherGroupCommand);
        yield return ("Settings", "Settings", "Ctrl+OemComma", vm => vm.OpenSettingsCommand);
        yield return ("CommandPalette", "Command palette", "Ctrl+Shift+P", vm => vm.ShowCommandPaletteCommand);
    }

    private static readonly Dictionary<Window, List<KeyBinding>> WindowBindings = [];

    /// <summary>Gives every editor command its overridden or default gestures (at startup and after Settings).</summary>
    public static void ApplyEditorOverrides(IReadOnlyDictionary<string, string> overrides)
    {
        _ = EditorDefaults.Count; // capture the defaults before anything is overridden
        foreach (var command in AllEditorCommands())
        {
            var text = overrides.TryGetValue(command.Name, out var custom) ? custom : string.Join("|", DefaultEditorGesture(command));
            command.InputGestures.Clear();
            foreach (var gesture in Parse(text))
            {
                command.InputGestures.Add(gesture);
            }
        }
    }

    /// <summary>(Re)creates the window's key bindings for application commands, honouring overrides.</summary>
    public static void BindApplicationCommands(Window window, MainViewModel vm, IReadOnlyDictionary<string, string> overrides)
    {
        if (WindowBindings.Remove(window, out var old))
        {
            foreach (var binding in old)
            {
                window.InputBindings.Remove(binding);
            }
        }

        var added = new List<KeyBinding>();
        foreach (var (name, _, defaults, command) in AppCommands())
        {
            var text = overrides.TryGetValue(name, out var custom) ? custom : defaults;
            foreach (var gesture in Parse(text))
            {
                var binding = new KeyBinding(command(vm), gesture);
                window.InputBindings.Add(binding);
                added.Add(binding);
            }
        }

        WindowBindings[window] = added;
    }

    /// <summary>Every shortcut, for the settings window's editor.</summary>
    public static IReadOnlyList<ShortcutRow> All(IReadOnlyDictionary<string, string> overrides)
    {
        var list = AppCommands()
            .Select(c => new ShortcutRow(c.Name, c.Description, c.Gesture, overrides.TryGetValue(c.Name, out var o) ? o : c.Gesture))
            .ToList();
        list.Add(new ShortcutRow("FindInNotebooks", "Find in notebooks", "Ctrl+Shift+F", "Ctrl+Shift+F", editable: false));
        list.AddRange(AllEditorCommands().Select(c =>
        {
            var defaults = string.Join("|", DefaultEditorGesture(c));
            return new ShortcutRow(c.Name, c.Text.TrimEnd('…'), defaults, overrides.TryGetValue(c.Name, out var o) ? o : defaults,
                markdownOnly: EditorCommands.MarkdownOnly.Contains(c));
        }));
        return list;
    }

    /// <summary>Every application and editor command with its current first shortcut, for the command palette.</summary>
    public static IEnumerable<(string Description, ICommand Command, string Gesture)> Commands(MainViewModel vm, IReadOnlyDictionary<string, string> overrides)
    {
        foreach (var (name, description, defaults, command) in AppCommands())
        {
            yield return (description, command(vm), First(overrides.TryGetValue(name, out var custom) ? custom : defaults));
        }

        foreach (var command in AllEditorCommands())
        {
            yield return (command.Text.TrimEnd('…'), command, GestureText(command));
        }
    }

    /// <summary>
    /// The shortcut a menu item should show for <paramref name="command"/>: the first effective gesture of an
    /// application or routed command, or null when the command isn't one whose shortcut is known here.
    /// </summary>
    public static string? GestureText(ICommand command, MainViewModel vm, IReadOnlyDictionary<string, string> overrides)
    {
        if (command is RoutedCommand routed)
        {
            return GestureText(routed);
        }

        foreach (var (name, _, defaults, factory) in AppCommands())
        {
            if (ReferenceEquals(factory(vm), command))
            {
                return First(overrides.TryGetValue(name, out var custom) ? custom : defaults);
            }
        }

        return null;
    }

    private static string GestureText(RoutedCommand command) =>
        command.InputGestures.OfType<KeyGesture>().Select(g => Pretty(Format(g))).FirstOrDefault() ?? "";

    private static string First(string gestures) => Pretty(gestures.Split('|')[0]);

    /// <summary>
    /// Marks rows that share a gesture with another row: a conflict when both apply in the same places, a note when
    /// one is Markdown-only formatting and the other isn't (the editor resolves those by document type).
    /// </summary>
    public static void FindConflicts(IReadOnlyList<ShortcutRow> rows)
    {
        var owners = new Dictionary<string, List<ShortcutRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            foreach (var gesture in Parse(row.Gesture).Select(Format).Distinct())
            {
                (owners.TryGetValue(gesture, out var list) ? list : owners[gesture] = []).Add(row);
            }
        }

        foreach (var row in rows)
        {
            var others = Parse(row.Gesture).Select(Format)
                .SelectMany(g => owners.GetValueOrDefault(g) ?? [])
                .Where(o => o != row)
                .Distinct()
                .ToList();
            var conflicts = others.Where(o => o.MarkdownOnly == row.MarkdownOnly).Select(o => o.Description).ToList();
            var shared = others.Where(o => o.MarkdownOnly != row.MarkdownOnly).Select(o => o.Description).ToList();
            row.Conflict = conflicts.Count == 0 ? null : "Also used by " + string.Join(", ", conflicts);
            row.Note = shared.Count == 0 ? null
                : row.MarkdownOnly ? "Outside Markdown pages: " + string.Join(", ", shared)
                : "In Markdown pages: " + string.Join(", ", shared);
        }
    }

    /// <summary>The gesture for a key press, in KeyGesture syntax; null for modifier keys alone or keys that need a modifier.</summary>
    public static string? GestureFor(Key key, ModifierKeys modifiers)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System or Key.None)
        {
            return null;
        }

        try
        {
            return Format(new KeyGesture(key, modifiers));
        }
        catch (NotSupportedException)
        {
            return null; // letters and digits need Ctrl or Alt
        }
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

    /// <summary>Readable key names: "Ctrl+OemPlus|Ctrl+Add" → "Ctrl++, Ctrl+Num +".</summary>
    public static string Pretty(string text) => string.Join(", ", text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(gesture => KeyName().Replace(gesture, m => m.Value switch
        {
            "OemPlus" => "+",
            "OemMinus" => "-",
            "OemComma" => ",",
            "OemPeriod" => ".",
            "OemQuestion" => "/",
            "Oem5" => "\\",
            "Oem3" => "`",
            "OemOpenBrackets" => "[",
            "OemCloseBrackets" => "]",
            "Add" => "Num +",
            "Subtract" => "Num -",
            "Next" => "PageDown",
            "Prior" => "PageUp",
            _ when m.Value.Length == 2 && m.Value[0] == 'D' && char.IsDigit(m.Value[1]) => m.Value[1..],
            _ => m.Value,
        })));

    [GeneratedRegex(@"(?<=^|\+)[A-Za-z0-9]+$")]
    private static partial Regex KeyName();
}
