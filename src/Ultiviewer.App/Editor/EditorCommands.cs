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

    // OneNote's "Basic Text" group, as Markdown (only in Markdown documents).
    public static RoutedUICommand Bold { get; } = Create("Bold", nameof(Bold), Key.B, ModifierKeys.Control);
    public static RoutedUICommand Italic { get; } = Create("Italic", nameof(Italic), Key.I, ModifierKeys.Control);
    public static RoutedUICommand Underline { get; } = Create("Underline", nameof(Underline), Key.U, ModifierKeys.Control);
    public static RoutedUICommand Strikethrough { get; } = Create("Strikethrough", nameof(Strikethrough), Key.OemMinus, ModifierKeys.Control);
    public static RoutedUICommand Subscript { get; } = Create("Subscript", nameof(Subscript));
    public static RoutedUICommand Superscript { get; } = Create("Superscript", nameof(Superscript));
    public static RoutedUICommand Highlight { get; } = Create("Highlight", nameof(Highlight), Key.H, ModifierKeys.Control | ModifierKeys.Alt);
    public static RoutedUICommand InlineCode { get; } = Create("Inline code", nameof(InlineCode), Key.Oem3, ModifierKeys.Control);
    public static RoutedUICommand ClearFormatting { get; } = Create("Clear formatting", nameof(ClearFormatting), Key.Space, ModifierKeys.Control);
    public static RoutedUICommand NormalText { get; } = Create("Normal text", nameof(NormalText), Key.D0, ModifierKeys.Control | ModifierKeys.Alt);
    public static RoutedUICommand Heading1 { get; } = Create("Heading 1", nameof(Heading1), Key.D1, ModifierKeys.Control | ModifierKeys.Alt);
    public static RoutedUICommand Heading2 { get; } = Create("Heading 2", nameof(Heading2), Key.D2, ModifierKeys.Control | ModifierKeys.Alt);
    public static RoutedUICommand Heading3 { get; } = Create("Heading 3", nameof(Heading3), Key.D3, ModifierKeys.Control | ModifierKeys.Alt);
    public static RoutedUICommand Heading4 { get; } = Create("Heading 4", nameof(Heading4), Key.D4, ModifierKeys.Control | ModifierKeys.Alt);
    public static RoutedUICommand Heading5 { get; } = Create("Heading 5", nameof(Heading5), Key.D5, ModifierKeys.Control | ModifierKeys.Alt);
    public static RoutedUICommand Heading6 { get; } = Create("Heading 6", nameof(Heading6), Key.D6, ModifierKeys.Control | ModifierKeys.Alt);
    public static RoutedUICommand Quote { get; } = Create("Quote", nameof(Quote));
    public static RoutedUICommand CodeBlock { get; } = Create("Code block", nameof(CodeBlock));
    public static RoutedUICommand BulletList { get; } = Create("Bulleted list", nameof(BulletList), Key.OemPeriod, ModifierKeys.Control);
    public static RoutedUICommand NumberedList { get; } = Create("Numbered list", nameof(NumberedList), Key.OemQuestion, ModifierKeys.Control);
    public static RoutedUICommand InsertLink { get; } = Create("Link…", nameof(InsertLink), Key.K, ModifierKeys.Control);
    public static RoutedUICommand InsertTable { get; } = Create("Table", nameof(InsertTable));
    public static RoutedUICommand HorizontalRule { get; } = Create("Horizontal line", nameof(HorizontalRule));

    /// <summary>Commands that only apply to Markdown documents; their shortcuts win there (Ctrl+U underlines in notes, lowercases elsewhere).</summary>
    public static IReadOnlySet<RoutedUICommand> MarkdownOnly { get; } = new HashSet<RoutedUICommand>
    {
        Bold, Italic, Underline, Strikethrough, Subscript, Superscript, Highlight, InlineCode, ClearFormatting,
        NormalText, Heading1, Heading2, Heading3, Heading4, Heading5, Heading6, Quote, CodeBlock,
        BulletList, NumberedList, InsertLink, InsertTable, HorizontalRule,
    };

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
