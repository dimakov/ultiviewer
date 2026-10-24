using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;

namespace Ultiviewer.App.Editor.Live;

/// <summary>
/// Hidden marks (the default): only the formatting shows, also at the caret. <see cref="Core.Markdown.LiveMarkup"/>
/// decides the rules; this part applies them to the caret and to Backspace, Delete, Enter and the arrow keys.
/// </summary>
public sealed partial class LivePreview
{
    private bool _hideMarks = true;
    private bool _snapping;

    /// <summary>Marks stay hidden at the caret; false shows them around it, as Obsidian does.</summary>
    public bool HideMarks
    {
        get => _hideMarks;
        set
        {
            if (_hideMarks != value)
            {
                _hideMarks = value;
                _revealed = null;
                if (IsEnabled)
                {
                    TextView.Redraw();
                }
            }
        }
    }

    private bool HidingMarks => IsEnabled && _hideMarks && Markup is not null;

    /// <summary>Called before Enter splits a line: steps out of formatting at its edges, or closes and reopens it inside.</summary>
    public void PrepareEnter()
    {
        if (!HidingMarks || !_editor.TextArea.Selection.IsEmpty || _document is null)
        {
            return;
        }

        var caret = _editor.CaretOffset;
        var (target, insert, at) = Markup!.Enter(caret);
        if (insert.Length > 0)
        {
            _document.Insert(target, insert);
            MoveCaret(target + at);
        }
        else if (target != caret)
        {
            MoveCaret(target);
        }
    }

    /// <summary>Keeps an empty caret out of hidden prefixes and marks.</summary>
    private bool SnapCaret()
    {
        var area = _editor.TextArea;
        if (_snapping || !HidingMarks || !area.Selection.IsEmpty)
        {
            return false;
        }

        var offset = area.Caret.Offset;
        var snapped = Markup!.SnapCaret(offset);
        if (snapped == offset)
        {
            return false;
        }

        MoveCaret(snapped);
        return true;
    }

    /// <summary>Backspace, Delete, ← and → with hidden marks; true when the key was handled here.</summary>
    private bool HandleHiddenMarksKey(KeyEventArgs e)
    {
        var area = _editor.TextArea;
        if (!HidingMarks || !area.Selection.IsEmpty || _document is null || Keyboard.Modifiers != ModifierKeys.None)
        {
            return false;
        }

        var markup = Markup!;
        var caret = area.Caret.Offset;
        switch (e.Key)
        {
            case Key.Back:
            {
                var (target, remove) = markup.Backspace(caret);
                if (remove is { } range)
                {
                    _document.Remove(range.Start, range.Length);
                    MoveCaret(range.Start);
                    return true;
                }

                MoveCaret(target);
                return false; // AvalonEdit deletes the character before the caret
            }

            case Key.Delete:
            {
                var (target, remove) = markup.Delete(caret);
                if (remove is { } range)
                {
                    _document.Remove(range.Start, range.Length);
                    MoveCaret(range.Start);
                    return true;
                }

                MoveCaret(target);
                return false;
            }

            case Key.Left when caret > 0 && caret == markup.ProseStart(caret) && _document.GetLineByOffset(caret) is { Offset: var lineStart } line && caret > lineStart:
                // From the start of a heading's or list item's text, ← goes to the end of the previous line.
                if (line.PreviousLine is { } previous)
                {
                    MoveCaret(previous.EndOffset);
                }

                return true;
            case Key.Left or Key.Right:
                return MoveSkippingHiddenMarks(e.Key == Key.Left ? EditingCommands.MoveLeftByCharacter : EditingCommands.MoveRightByCharacter);
            default:
                return false;
        }
    }

    /// <summary>
    /// Moves the caret one character, again when it only crossed hidden marks (it would not visibly move). At the end
    /// of a line the last position on it is kept.
    /// </summary>
    private bool MoveSkippingHiddenMarks(RoutedCommand command)
    {
        var area = _editor.TextArea;
        var line = area.Caret.Line;
        var x = area.Caret.CalculateCaretRectangle().X;
        command.Execute(null, area);
        for (var i = 0; i < 4 && area.Caret.Line == line && Math.Abs(area.Caret.CalculateCaretRectangle().X - x) < 0.5; i++)
        {
            var position = area.Caret.Position;
            var offset = area.Caret.Offset;
            command.Execute(null, area);
            if (area.Caret.Offset == offset)
            {
                break;
            }

            if (area.Caret.Line != line)
            {
                area.Caret.Position = position;
                break;
            }
        }

        return true;
    }

    private void MoveCaret(int offset)
    {
        _snapping = true;
        try
        {
            _editor.TextArea.Caret.Offset = Math.Clamp(offset, 0, _document?.TextLength ?? 0);
        }
        finally
        {
            _snapping = false;
        }
    }
}
