using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.Document;
using Ultiviewer.App.Editor;
using Ultiviewer.Core.Markdown;
using Ultiviewer.Core.Settings;

namespace Ultiviewer.App.Views;

/// <summary>OneNote's "Basic Text" formatting for Markdown documents: toolbar, Format menu and shortcuts.</summary>
public partial class EditorHost
{
    private FormatBar? _formatBar;

    private static readonly Regex ListLine = new(@"^[ \t]*(?:[-*+]|\d{1,9}[.)])[ \t]", RegexOptions.Compiled);

    private void InitializeFormatting()
    {
        _formatBar = new FormatBar(Editor.TextArea);
        FormatHost.Content = _formatBar;
        _formatBar.StyleChosen += SetParagraphStyle;
        _formatBar.ColorChosen += SetColor;
        Editor.TextArea.Caret.PositionChanged += (_, _) => _formatBar.ShowStyle(CurrentParagraphStyle());
        TabShown += _ => UpdateFormatBar();

        Bind(EditorCommands.Bold, (_, _) => ToggleInline(InlineStyle.Bold));
        Bind(EditorCommands.Italic, (_, _) => ToggleInline(InlineStyle.Italic));
        Bind(EditorCommands.Underline, (_, _) => ToggleInline(InlineStyle.Underline));
        Bind(EditorCommands.Strikethrough, (_, _) => ToggleInline(InlineStyle.Strikethrough));
        Bind(EditorCommands.Subscript, (_, _) => ToggleInline(InlineStyle.Subscript));
        Bind(EditorCommands.Superscript, (_, _) => ToggleInline(InlineStyle.Superscript));
        Bind(EditorCommands.Highlight, (_, _) => ToggleInline(InlineStyle.Highlight));
        Bind(EditorCommands.InlineCode, (_, _) => ToggleInline(InlineStyle.Code));
        Bind(EditorCommands.ClearFormatting, (_, _) => ClearFormatting());
        Bind(EditorCommands.NormalText, (_, _) => SetParagraphStyle(0));
        Bind(EditorCommands.Heading1, (_, _) => SetParagraphStyle(1));
        Bind(EditorCommands.Heading2, (_, _) => SetParagraphStyle(2));
        Bind(EditorCommands.Heading3, (_, _) => SetParagraphStyle(3));
        Bind(EditorCommands.Heading4, (_, _) => SetParagraphStyle(4));
        Bind(EditorCommands.Heading5, (_, _) => SetParagraphStyle(5));
        Bind(EditorCommands.Heading6, (_, _) => SetParagraphStyle(6));
        Bind(EditorCommands.Quote, (_, _) => TransformLines(lines => MarkdownFormatting.ToggleList(lines, ListStyle.Quote)));
        Bind(EditorCommands.BulletList, (_, _) => TransformSelectedLines(lines => MarkdownFormatting.ToggleList(lines, ListStyle.Bullet)));
        Bind(EditorCommands.NumberedList, (_, _) => TransformSelectedLines(lines => MarkdownFormatting.ToggleList(lines, ListStyle.Numbered)));
        Bind(EditorCommands.CodeBlock, (_, _) => InsertCodeBlock());
        Bind(EditorCommands.InsertLink, (_, _) => InsertLink());
        Bind(EditorCommands.InsertTable, (_, _) => InsertBlock(MarkdownFormatting.Table(NewLineAt(Editor.TextArea.Caret.Line)), selectFirstCell: true));
        Bind(EditorCommands.HorizontalRule, (_, _) => InsertBlock("---" + NewLineAt(Editor.TextArea.Caret.Line), selectFirstCell: false));
    }

    /// <summary>The toolbar shows for Markdown documents that are being edited (not in Preview-only mode).</summary>
    private void UpdateFormatBar()
    {
        if (_formatBar is null)
        {
            return;
        }

        var show = (_vm?.Settings.ShowFormatBar ?? true) && _current is { IsMarkdown: true } tab && tab.ViewMode != ViewMode.Preview;
        _formatBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            _formatBar.ShowStyle(CurrentParagraphStyle());
        }
    }

    private void Apply(TextEdit edit)
    {
        using (Editor.Document.RunUpdate())
        {
            Editor.Document.Replace(edit.Start, edit.Length, edit.Replacement);
        }

        Editor.Select(edit.SelectionStart, edit.SelectionLength);
        Editor.TextArea.Caret.BringCaretToView();
    }

    private void ToggleInline(InlineStyle style) =>
        Apply(MarkdownFormatting.ToggleInline(Editor.Document.Text, Editor.SelectionStart, Editor.SelectionLength, style));

    private void SetColor(string? color) =>
        Apply(MarkdownFormatting.SetColor(Editor.Document.Text, Editor.SelectionStart, Editor.SelectionLength, color));

    /// <summary>0 = normal text, 1–6 = heading level, applied to every selected line.</summary>
    private void SetParagraphStyle(int level) =>
        TransformSelectedLines(lines => lines.Select(l => l.Trim().Length == 0 ? l : MarkdownFormatting.SetHeading(l, level)).ToList());

    private int CurrentParagraphStyle()
    {
        var document = Editor.Document;
        if (document.LineCount == 0)
        {
            return 0;
        }

        var text = document.GetText(document.GetLineByNumber(Editor.TextArea.Caret.Line));
        var hashes = text.TrimStart(' ').TakeWhile(c => c == '#').Count();
        return hashes is >= 1 and <= 6 && text.TrimStart(' ').Length > hashes && text.TrimStart(' ')[hashes] is ' ' or '\t' ? hashes : 0;
    }

    /// <summary>Like <see cref="TransformLines"/> but limited to the caret line when nothing is selected.</summary>
    private void TransformSelectedLines(Func<IReadOnlyList<string>, IReadOnlyList<string>> transform)
    {
        var document = Editor.Document;
        var (first, last) = SelectedLines();
        var lines = Enumerable.Range(first.LineNumber, last.LineNumber - first.LineNumber + 1)
            .Select(n => document.GetText(document.GetLineByNumber(n))).ToList();
        var caretColumn = Editor.CaretOffset - first.Offset;
        var hadSelection = Editor.SelectionLength > 0;
        var result = string.Join(NewLineAt(first.LineNumber), transform(lines));
        var start = first.Offset;
        document.Replace(start, last.EndOffset - start, result);
        if (hadSelection)
        {
            Editor.Select(start, result.Length);
        }
        else
        {
            // Keep the caret on the line, after the new prefix.
            var newLine = document.GetLineByOffset(start);
            Editor.CaretOffset = Math.Min(newLine.EndOffset, start + Math.Max(0, caretColumn + result.Length - (lines[0].Length)));
        }
    }

    private void ClearFormatting()
    {
        if (Editor.SelectionLength == 0)
        {
            var line = Editor.Document.GetLineByOffset(Editor.CaretOffset);
            Editor.Select(line.Offset, line.Length);
        }

        var start = Editor.SelectionStart;
        var cleared = MarkdownFormatting.ClearFormatting(Editor.SelectedText);
        Apply(new TextEdit(start, Editor.SelectionLength, cleared, start, cleared.Length));
    }

    private void InsertLink()
    {
        string? url = null;
        try
        {
            var clipboard = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
            if (Uri.TryCreate(clipboard, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
            {
                url = clipboard;
            }
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
        }

        Apply(MarkdownFormatting.MakeLink(Editor.Document.Text, Editor.SelectionStart, Editor.SelectionLength, url));
    }

    private void InsertCodeBlock()
    {
        var newLine = NewLineAt(Editor.TextArea.Caret.Line);
        if (Editor.SelectionLength > 0)
        {
            var (first, last) = SelectedLines();
            var block = Editor.Document.GetText(first.Offset, last.EndOffset - first.Offset);
            var wrapped = MarkdownFormatting.CodeBlock(block, newLine);
            Apply(new TextEdit(first.Offset, last.EndOffset - first.Offset, wrapped, first.Offset + 3, 0));
        }
        else
        {
            InsertBlock("```" + newLine + newLine + "```" + newLine, selectFirstCell: false, caretFromStart: 3 + newLine.Length);
        }
    }

    /// <summary>Inserts a block on its own line(s) below the caret line (or on it, when it is empty).</summary>
    private void InsertBlock(string block, bool selectFirstCell, int? caretFromStart = null)
    {
        var document = Editor.Document;
        var line = document.GetLineByOffset(Editor.CaretOffset);
        var newLine = NewLineAt(line.LineNumber);
        var empty = document.GetText(line).Trim().Length == 0;
        var insertAt = empty ? line.Offset : line.EndOffset;
        var text = empty ? block : newLine + newLine + block;
        document.Insert(insertAt, text);
        var blockStart = insertAt + (empty ? 0 : newLine.Length * 2);
        if (selectFirstCell)
        {
            Editor.Select(blockStart + 2, "Column 1".Length);
        }
        else
        {
            Editor.CaretOffset = blockStart + (caretFromStart ?? block.Length);
        }
    }

    /// <summary>Tab / Shift+Tab on a list item indent or outdent the whole item, like OneNote.</summary>
    private bool HandleListIndent(KeyEventArgs e)
    {
        if (e.Key != Key.Tab || Keyboard.Modifiers is not (ModifierKeys.None or ModifierKeys.Shift) || Editor.SelectionLength > 0)
        {
            return false;
        }

        var document = Editor.Document;
        var line = document.GetLineByOffset(Editor.CaretOffset);
        var text = document.GetText(line);
        if (!ListLine.IsMatch(text))
        {
            return false;
        }

        var caret = Editor.CaretOffset - line.Offset;
        if (Keyboard.Modifiers == ModifierKeys.None)
        {
            document.Insert(line.Offset, Editor.Options.IndentationString);
            Editor.CaretOffset = line.Offset + caret + Editor.Options.IndentationString.Length;
            return true;
        }

        var remove = text.StartsWith('\t') ? 1 : text.TakeWhile(c => c == ' ').Take(Editor.Options.IndentationSize).Count();
        if (remove > 0)
        {
            document.Remove(line.Offset, remove);
            Editor.CaretOffset = line.Offset + Math.Max(0, caret - remove);
        }

        return true;
    }
}
