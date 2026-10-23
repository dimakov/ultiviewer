using System.Windows.Input;

namespace Ultiviewer.App.Editor;

/// <summary>
/// Editor commands with Notepad++ / OneNote shortcuts. They are handled by <c>EditorHost</c>, so they apply
/// to the editor with keyboard focus and are disabled in menus otherwise.
/// </summary>
public static class EditorCommands
{
    public static RoutedUICommand Find { get; } = Create("Find…", nameof(Find), Key.F, ModifierKeys.Control);
    public static RoutedUICommand Replace { get; } = Create("Replace…", nameof(Replace), Key.H, ModifierKeys.Control);
    public static RoutedUICommand FindNext { get; } = Create("Find next", nameof(FindNext), Key.F3, ModifierKeys.None);
    public static RoutedUICommand FindPrevious { get; } = Create("Find previous", nameof(FindPrevious), Key.F3, ModifierKeys.Shift);
    public static RoutedUICommand GoToLine { get; } = Create("Go to line…", nameof(GoToLine), Key.G, ModifierKeys.Control);

    public static RoutedUICommand DuplicateLine { get; } = Create("Duplicate line", nameof(DuplicateLine), Key.D, ModifierKeys.Control);
    public static RoutedUICommand DeleteLine { get; } = Create("Delete line", nameof(DeleteLine), Key.L, ModifierKeys.Control | ModifierKeys.Shift);
    public static RoutedUICommand MoveLineUp { get; } = Create("Move line up", nameof(MoveLineUp), Key.Up, ModifierKeys.Control | ModifierKeys.Shift);
    public static RoutedUICommand MoveLineDown { get; } = Create("Move line down", nameof(MoveLineDown), Key.Down, ModifierKeys.Control | ModifierKeys.Shift);
    public static RoutedUICommand JoinLines { get; } = Create("Join lines", nameof(JoinLines), Key.J, ModifierKeys.Control);
    public static RoutedUICommand SortAscending { get; } = Create("Sort lines ascending", nameof(SortAscending));
    public static RoutedUICommand SortDescending { get; } = Create("Sort lines descending", nameof(SortDescending));
    public static RoutedUICommand RemoveDuplicateLines { get; } = Create("Remove duplicate lines", nameof(RemoveDuplicateLines));
    public static RoutedUICommand RemoveEmptyLines { get; } = Create("Remove empty lines", nameof(RemoveEmptyLines));
    public static RoutedUICommand TrimTrailing { get; } = Create("Trim trailing whitespace", nameof(TrimTrailing));

    public static RoutedUICommand UpperCase { get; } = Create("UPPERCASE", nameof(UpperCase), Key.U, ModifierKeys.Control | ModifierKeys.Shift);
    public static RoutedUICommand LowerCase { get; } = Create("lowercase", nameof(LowerCase), Key.U, ModifierKeys.Control);
    public static RoutedUICommand TitleCase { get; } = Create("Title Case", nameof(TitleCase));

    public static RoutedUICommand ToggleBookmark { get; } = Create("Toggle bookmark", nameof(ToggleBookmark), Key.F2, ModifierKeys.Control);
    public static RoutedUICommand NextBookmark { get; } = Create("Next bookmark", nameof(NextBookmark), Key.F2, ModifierKeys.None);
    public static RoutedUICommand PreviousBookmark { get; } = Create("Previous bookmark", nameof(PreviousBookmark), Key.F2, ModifierKeys.Shift);
    public static RoutedUICommand ClearBookmarks { get; } = Create("Clear all bookmarks", nameof(ClearBookmarks));

    public static RoutedUICommand ToggleTask { get; } = Create("Toggle to-do", nameof(ToggleTask), Key.D1, ModifierKeys.Control);
    public static RoutedUICommand InsertDate { get; } = Create("Insert date", nameof(InsertDate), Key.D, ModifierKeys.Alt | ModifierKeys.Shift);
    public static RoutedUICommand InsertTime { get; } = Create("Insert time", nameof(InsertTime), Key.T, ModifierKeys.Alt | ModifierKeys.Shift);
    public static RoutedUICommand InsertDateTime { get; } = Create("Insert date and time", nameof(InsertDateTime), Key.F, ModifierKeys.Alt | ModifierKeys.Shift);

    /// <summary>All commands that carry a keyboard shortcut, for binding and conflict removal.</summary>
    public static IEnumerable<RoutedUICommand> WithGestures => typeof(EditorCommands)
        .GetProperties()
        .Where(p => p.PropertyType == typeof(RoutedUICommand))
        .Select(p => (RoutedUICommand)p.GetValue(null)!)
        .Where(c => c.InputGestures.Count > 0);

    private static RoutedUICommand Create(string text, string name, Key key = Key.None, ModifierKeys modifiers = ModifierKeys.None)
    {
        var gestures = new InputGestureCollection();
        if (key != Key.None)
        {
            gestures.Add(new KeyGesture(key, modifiers));
        }

        return new RoutedUICommand(text, name, typeof(EditorCommands), gestures);
    }
}
