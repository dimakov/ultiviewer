using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.Document;
using Ultiviewer.App.Editor;
using Ultiviewer.Core.Markdown;
using Ultiviewer.Core.Text;

namespace Ultiviewer.App.Views;

/// <summary>Editor commands: find bar, go to line, line operations, case, bookmarks, to-dos and dates.</summary>
public partial class EditorHost
{
    private FindBar? _findBar;
    private SearchHighlighter? _highlighter;

    private void InitializeCommands()
    {
        _highlighter = new SearchHighlighter { View = Editor.TextArea.TextView };
        Editor.TextArea.TextView.BackgroundRenderers.Add(_highlighter);
        _findBar = new FindBar(Editor, _highlighter);
        _findBar.Closed += FocusEditor;
        BarHost.Content = _findBar;
        Editor.TextArea.LeftMargins.Insert(0, new BookmarkMargin(() => _current));
        Editor.TextArea.SelectionChanged += (_, _) => UpdateSmartHighlight();
        Editor.TextChanged += (_, _) =>
        {
            _findBar.DocumentChanged();
            _highlighter.SetSmart([]);
        };
        TabShown += _ => _findBar.DocumentChanged();

        RemoveConflictingAvalonBindings();
        PreviewKeyDown += OnPreviewKeyDownForCommands;
        Bind(EditorCommands.Find, (_, _) => _findBar.Open(replace: false));
        Bind(EditorCommands.Replace, (_, _) => _findBar.Open(replace: true));
        Bind(EditorCommands.FindNext, (_, _) => FindAgain(forward: true));
        Bind(EditorCommands.FindPrevious, (_, _) => FindAgain(forward: false));
        Bind(EditorCommands.GoToLine, (_, _) => PromptGoToLine());
        Bind(EditorCommands.DuplicateLine, (_, _) => DuplicateLine());
        Bind(EditorCommands.DeleteLine, (_, _) => DeleteLines());
        Bind(EditorCommands.MoveLineUp, (_, _) => MoveLines(up: true));
        Bind(EditorCommands.MoveLineDown, (_, _) => MoveLines(up: false));
        Bind(EditorCommands.JoinLines, (_, _) => JoinLines());
        Bind(EditorCommands.SortAscending, (_, _) => TransformLines(LineOperations.SortAscending));
        Bind(EditorCommands.SortDescending, (_, _) => TransformLines(LineOperations.SortDescending));
        Bind(EditorCommands.RemoveDuplicateLines, (_, _) => TransformLines(LineOperations.RemoveDuplicates));
        Bind(EditorCommands.RemoveEmptyLines, (_, _) => TransformLines(LineOperations.RemoveEmpty));
        Bind(EditorCommands.TrimTrailing, (_, _) => TransformLines(LineOperations.TrimTrailing));
        Bind(EditorCommands.UpperCase, (_, _) => TransformSelection(LineOperations.ToUpper));
        Bind(EditorCommands.LowerCase, (_, _) => TransformSelection(LineOperations.ToLower));
        Bind(EditorCommands.TitleCase, (_, _) => TransformSelection(LineOperations.ToTitleCase));
        Bind(EditorCommands.ToggleBookmark, (_, _) => ToggleBookmark());
        Bind(EditorCommands.NextBookmark, (_, _) => JumpToBookmark(forward: true));
        Bind(EditorCommands.PreviousBookmark, (_, _) => JumpToBookmark(forward: false));
        Bind(EditorCommands.ClearBookmarks, (_, _) =>
        {
            _current?.ClearBookmarks();
            Editor.TextArea.LeftMargins.OfType<BookmarkMargin>().FirstOrDefault()?.InvalidateVisual();
        });
        Bind(EditorCommands.ToggleTask, (_, _) => ToggleTaskOnCaretLine());
        Bind(EditorCommands.InsertDate, (_, _) => InsertText(DateTime.Now.ToString(_vm?.Settings.DateFormat ?? "yyyy-MM-dd")));
        Bind(EditorCommands.InsertTime, (_, _) => InsertText(DateTime.Now.ToString(_vm?.Settings.TimeFormat ?? "HH:mm")));
        Bind(EditorCommands.InsertDateTime, (_, _) =>
            InsertText(DateTime.Now.ToString((_vm?.Settings.DateFormat ?? "yyyy-MM-dd") + " " + (_vm?.Settings.TimeFormat ?? "HH:mm"))));
    }

    private void Bind(RoutedUICommand command, ExecutedRoutedEventHandler execute) =>
        CommandBindings.Add(new CommandBinding(command, execute, (_, e) => e.CanExecute =
            _current is not null && Editor.IsVisible && (_current.IsMarkdown || !EditorCommands.MarkdownOnly.Contains(command))));

    /// <summary>
    /// Our shortcuts run before the text area sees the key (it swallows arrow keys, and AvalonEdit maps
    /// Ctrl+D to its own "delete line"). In the find bar only the find/go-to shortcuts apply.
    /// </summary>
    private void OnPreviewKeyDownForCommands(object sender, KeyEventArgs e)
    {
        if (e.Handled || _current is null)
        {
            return;
        }

        var inEditor = e.OriginalSource is DependencyObject source && (ReferenceEquals(source, Editor.TextArea) || IsInside(source, Editor));
        // AltGr arrives as Ctrl+Alt; it types characters (₪, €…), so it never triggers Ctrl+Alt shortcuts.
        var altGr = Keyboard.IsKeyDown(Key.RightAlt);
        foreach (var command in EditorCommands.WithGestures.OrderBy(c => EditorCommands.MarkdownOnly.Contains(c) ? 0 : 1))
        {
            if (!inEditor && !FindBarCommands.Contains(command))
            {
                continue;
            }

            foreach (KeyGesture gesture in command.InputGestures)
            {
                if (altGr && gesture.Modifiers.HasFlag(ModifierKeys.Control | ModifierKeys.Alt))
                {
                    continue;
                }

                if (gesture.Matches(null, e) && command.CanExecute(null, Editor.TextArea))
                {
                    command.Execute(null, Editor.TextArea);
                    e.Handled = true;
                    return;
                }
            }
        }
    }

    private static readonly HashSet<RoutedUICommand> FindBarCommands =
    [
        EditorCommands.Find, EditorCommands.Replace, EditorCommands.FindNext, EditorCommands.FindPrevious, EditorCommands.GoToLine,
    ];

    private static bool IsInside(DependencyObject element, DependencyObject ancestor)
    {
        for (var current = element; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>AvalonEdit binds some of our keys (Ctrl+D deletes a line there); ours win.</summary>
    private void RemoveConflictingAvalonBindings()
    {
        var ours = EditorCommands.WithGestures.SelectMany(c => c.InputGestures.OfType<KeyGesture>())
            .Concat(Services.KeyboardShortcuts.DefaultEditorGestures())
            .ToList();
        bool Conflicts(KeyGesture g) => ours.Any(o => o.Key == g.Key && o.Modifiers == g.Modifiers);

        var handler = Editor.TextArea.DefaultInputHandler;
        foreach (var bindings in new[] { handler.Editing.InputBindings, handler.CaretNavigation.InputBindings })
        {
            foreach (var binding in bindings.OfType<KeyBinding>().Where(b => b.Gesture is KeyGesture g && Conflicts(g)).ToList())
            {
                bindings.Remove(binding);
            }
        }

        // Gestures declared on AvalonEdit's own routed commands (e.g. AvalonEditCommands.DeleteLine = Ctrl+D).
        foreach (var property in typeof(ICSharpCode.AvalonEdit.AvalonEditCommands).GetFields().Where(f => f.FieldType == typeof(RoutedCommand)))
        {
            var command = (RoutedCommand)property.GetValue(null)!;
            foreach (var gesture in command.InputGestures.OfType<KeyGesture>().Where(Conflicts).ToList())
            {
                command.InputGestures.Remove(gesture);
            }
        }
    }

    private void FindAgain(bool forward)
    {
        if (_findBar is null)
        {
            return;
        }

        if (!_findBar.IsOpen || _findBar.Pattern.Length == 0)
        {
            _findBar.Open(replace: false);
            return;
        }

        if (forward)
        {
            _findBar.FindNext();
        }
        else
        {
            _findBar.FindPrevious();
        }
    }

    private void PromptGoToLine()
    {
        var count = Editor.Document.LineCount;
        var dialog = new InputDialog("Go to line", $"Line number (1–{count}):", Editor.TextArea.Caret.Line.ToString(), text =>
            int.TryParse(text, out var n) && n >= 1 && n <= count ? null : $"Enter a number from 1 to {count}.")
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() == true && int.TryParse(dialog.Value, out var line))
        {
            GoToLine(line);
            FocusEditor();
        }
    }

    // ---- Line operations ----

    /// <summary>Lines covered by the selection (a selection ending at column 1 doesn't include that line).</summary>
    private (DocumentLine First, DocumentLine Last) SelectedLines()
    {
        var document = Editor.Document;
        var first = document.GetLineByOffset(Editor.SelectionStart);
        var last = document.GetLineByOffset(Editor.SelectionStart + Editor.SelectionLength);
        if (Editor.SelectionLength > 0 && last.LineNumber > first.LineNumber && Editor.SelectionStart + Editor.SelectionLength == last.Offset)
        {
            last = last.PreviousLine;
        }

        return (first, last);
    }

    private string NewLineAt(int lineNumber) => TextUtilities.GetNewLineFromDocument(Editor.Document, lineNumber);

    private void DuplicateLine()
    {
        var document = Editor.Document;
        if (Editor.SelectionLength > 0)
        {
            var end = Editor.SelectionStart + Editor.SelectionLength;
            var selected = Editor.SelectedText;
            document.Insert(end, selected);
            Editor.Select(end, selected.Length);
            return;
        }

        var (first, last) = SelectedLines();
        var block = document.GetText(first.Offset, last.EndOffset - first.Offset);
        var caretColumn = Editor.CaretOffset - first.Offset;
        document.Insert(last.EndOffset, NewLineAt(last.LineNumber) + block);
        Editor.CaretOffset = Math.Min(document.TextLength, last.NextLine!.Offset + caretColumn);
    }

    private void DeleteLines()
    {
        var document = Editor.Document;
        var (first, last) = SelectedLines();
        var start = first.Offset;
        var end = last.EndOffset + last.DelimiterLength;
        if (last.DelimiterLength == 0 && first.PreviousLine is { } previous)
        {
            start = previous.EndOffset; // last line: remove the preceding line break instead
        }

        document.Remove(start, end - start);
    }

    private void MoveLines(bool up)
    {
        var document = Editor.Document;
        var (first, last) = SelectedLines();

        // In Markdown a heading moves with its section and a list item with its children.
        if (_current is { IsMarkdown: true } && first == last
            && MarkdownOutline.MoveBlock(document.Text, Editor.CaretOffset, up, NewLineAt(first.LineNumber)) is { } block)
        {
            Apply(block);
            return;
        }

        var neighbour = up ? first.PreviousLine : last.NextLine;
        if (neighbour is null)
        {
            return;
        }

        var blockText = document.GetText(first.Offset, last.EndOffset - first.Offset);
        var neighbourText = document.GetText(neighbour.Offset, neighbour.Length);
        var newLine = NewLineAt(first.LineNumber);
        var selectionStart = Editor.SelectionStart - first.Offset;
        var selectionLength = Editor.SelectionLength;
        var caretInBlock = Editor.CaretOffset - first.Offset;

        using (document.RunUpdate())
        {
            if (up)
            {
                var start = neighbour.Offset;
                document.Replace(start, last.EndOffset - start, blockText + newLine + neighbourText);
                Editor.Select(start + selectionStart, selectionLength);
                Editor.CaretOffset = start + caretInBlock;
            }
            else
            {
                var start = first.Offset;
                document.Replace(start, neighbour.EndOffset - start, neighbourText + newLine + blockText);
                var newStart = start + neighbourText.Length + newLine.Length;
                Editor.Select(newStart + selectionStart, selectionLength);
                Editor.CaretOffset = newStart + caretInBlock;
            }
        }
    }

    private void JoinLines()
    {
        var document = Editor.Document;
        var (first, last) = SelectedLines();
        if (first == last)
        {
            if (last.NextLine is not { } next)
            {
                return;
            }

            last = next;
        }

        var lines = Enumerable.Range(first.LineNumber, last.LineNumber - first.LineNumber + 1)
            .Select(n => document.GetText(document.GetLineByNumber(n))).ToList();
        document.Replace(first.Offset, last.EndOffset - first.Offset, LineOperations.Join(lines));
    }

    /// <summary>Applies a line transform to the selected lines, or to the whole document if nothing is selected.</summary>
    private void TransformLines(Func<IReadOnlyList<string>, IReadOnlyList<string>> transform)
    {
        var document = Editor.Document;
        var (first, last) = Editor.SelectionLength > 0
            ? SelectedLines()
            : (document.GetLineByNumber(1), document.GetLineByNumber(document.LineCount));
        var lines = Enumerable.Range(first.LineNumber, last.LineNumber - first.LineNumber + 1)
            .Select(n => document.GetText(document.GetLineByNumber(n))).ToList();
        var result = string.Join(NewLineAt(first.LineNumber), transform(lines));
        var start = first.Offset;
        document.Replace(start, last.EndOffset - start, result);
        if (Editor.SelectionLength > 0 || first.LineNumber != last.LineNumber)
        {
            Editor.Select(start, result.Length);
        }
    }

    private void TransformSelection(Func<string, string> transform)
    {
        if (Editor.SelectionLength == 0)
        {
            SelectWordAtCaret();
        }

        if (Editor.SelectionLength == 0)
        {
            return;
        }

        var start = Editor.SelectionStart;
        var replaced = transform(Editor.SelectedText);
        Editor.Document.Replace(start, Editor.SelectionLength, replaced);
        Editor.Select(start, replaced.Length);
    }

    private void SelectWordAtCaret()
    {
        var document = Editor.Document;
        var offset = Editor.CaretOffset;
        var start = TextUtilities.GetNextCaretPosition(document, Math.Min(offset + 1, document.TextLength), LogicalDirection.Backward, CaretPositioningMode.WordStart);
        var end = TextUtilities.GetNextCaretPosition(document, start < 0 ? 0 : start, LogicalDirection.Forward, CaretPositioningMode.WordBorder);
        if (start >= 0 && end > start)
        {
            Editor.Select(start, end - start);
        }
    }

    // ---- Smart highlight ----

    private static readonly Regex SingleWord = new(@"^\w{2,100}$", RegexOptions.CultureInvariant);

    private void UpdateSmartHighlight()
    {
        if (_highlighter is null)
        {
            return;
        }

        var selected = Editor.SelectionLength is > 1 and <= 100 ? Editor.SelectedText : "";
        if (!SingleWord.IsMatch(selected) || _findBar?.IsOpen == true)
        {
            _highlighter.SetSmart([]);
            return;
        }

        var regex = TextSearcher.Build(selected, new FindOptions(MatchCase: true, WholeWord: true));
        var matches = TextSearcher.FindAll(Editor.Document.Text, regex, max: 2000);
        _highlighter.SetSmart(matches.Count > 1 ? matches : []);
    }

    // ---- Bookmarks ----

    private void ToggleBookmark()
    {
        _current?.ToggleBookmark(Editor.TextArea.Caret.Line);
        Editor.TextArea.LeftMargins.OfType<BookmarkMargin>().FirstOrDefault()?.InvalidateVisual();
    }

    private void JumpToBookmark(bool forward)
    {
        if (_current?.BookmarkLines() is not { Count: > 0 } lines)
        {
            return;
        }

        var caretLine = Editor.TextArea.Caret.Line;
        var target = forward
            ? lines.FirstOrDefault(l => l > caretLine, lines[0])
            : lines.LastOrDefault(l => l < caretLine, lines[^1]);
        GoToLine(target);
    }

    // ---- OneNote conveniences ----

    /// <summary>Ctrl+1: plain line → open to-do → done → plain list item, on every selected line.</summary>
    private void ToggleTaskOnCaretLine()
    {
        var document = Editor.Document;
        var (first, last) = SelectedLines();
        using (document.RunUpdate())
        {
            for (var number = last.LineNumber; number >= first.LineNumber; number--)
            {
                var line = document.GetLineByNumber(number);
                var text = document.GetText(line);
                var cycled = TaskMarkers.Cycle(text);
                if (cycled != text)
                {
                    document.Replace(line.Offset, line.Length, cycled);
                }
            }
        }
    }

    private void InsertText(string text)
    {
        Editor.TextArea.Selection.ReplaceSelectionWithText(text);
        Editor.TextArea.Caret.BringCaretToView();
    }
}
