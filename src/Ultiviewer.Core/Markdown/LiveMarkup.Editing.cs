using System.Text.RegularExpressions;

namespace Ultiviewer.Core.Markdown;

/// <summary>
/// Editing with hidden marks, the Live view's default. Once text is formatted only the formatting shows:
/// <c>**</c>, <c>#</c>, <c>&gt;</c>, link syntax and list markers stay hidden even at the caret (block syntax
/// still opens for editing: tables, pictures and rules at the caret, code fences on their own line). The rules
/// below keep the caret and the edits from breaking the hidden syntax:
/// <list type="bullet">
/// <item>the caret never stands in a line's hidden prefix (<c>## </c>, <c>- [ ] </c>, <c>&gt; </c>) or inside a mark;</item>
/// <item>Backspace and Delete skip hidden marks and delete the visible character beyond them; deleting the last
/// character of a formatted run removes its marks too; Backspace at the start of a heading, quote or list item
/// removes that prefix;</item>
/// <item>Enter at the end of formatted text leaves the formatting behind; inside it, the marks are closed before
/// the line break and reopened after it.</item>
/// </list>
/// </summary>
public sealed partial class LiveMarkup
{
    /// <summary>Character styles enclosed by marks that open and close on the same line.</summary>
    private static readonly HashSet<LiveStyle> RunStyles =
    [
        LiveStyle.Strong, LiveStyle.Emphasis, LiveStyle.Strikethrough, LiveStyle.Underline, LiveStyle.Highlight,
        LiveStyle.Subscript, LiveStyle.Superscript, LiveStyle.Code, LiveStyle.Math, LiveStyle.Link, LiveStyle.Color,
    ];

    /// <summary>Whether <paramref name="span"/> stays hidden at the caret when marks are hidden.</summary>
    public bool HidesAtCaret(LiveSpan span) =>
        span.IsReplacement && span.Length > 0 && span.Style is not (LiveStyle.Table or LiveStyle.Image or LiveStyle.Rule) && !IsBlockMarker(span);

    /// <summary>
    /// Whether a caret or selection shows <paramref name="span"/> as source when marks are hidden: never for
    /// inline and prefix syntax, on its own line for block marks (code fences), as usual for widgets.
    /// </summary>
    public bool IsRevealedWithHiddenMarks(LiveSpan span, int selectionStart, int selectionEnd)
    {
        if (HidesAtCaret(span))
        {
            return false;
        }

        if (IsBlockMarker(span))
        {
            var (start, end) = LineRange(span.Start);
            return selectionStart <= end && selectionEnd >= start;
        }

        return span.IsRevealedBy(selectionStart, selectionEnd);
    }

    /// <summary>Where a line's text starts after its hidden prefix (and any hidden opening marks); the line start without one.</summary>
    public int ProseStart(int offset)
    {
        var line = LineOf(offset);
        var position = _lineStarts[line];
        var end = _lineEnds[line];
        var hidden = HiddenSpans(line);
        var consumed = false;
        while (true)
        {
            var skipped = position;
            while (skipped < end && _text[skipped] is ' ' or '\t')
            {
                skipped++;
            }

            var next = hidden.FirstOrDefault(s => s.Start == skipped);
            if (next.Length == 0)
            {
                return consumed ? skipped : _lineStarts[line];
            }

            position = next.End;
            consumed = true;
        }
    }

    /// <summary>The nearest caret position outside hidden syntax: after a line's prefix, at the edges of a mark.</summary>
    public int SnapCaret(int offset)
    {
        var prose = ProseStart(offset);
        if (offset < prose && offset >= _lineStarts[LineOf(offset)])
        {
            return prose;
        }

        foreach (var span in HiddenSpans(LineOf(offset)))
        {
            if (span.Start < offset && offset < span.End)
            {
                return IsClosingMark(span) ? span.Start : span.End;
            }
        }

        return offset;
    }

    /// <summary>
    /// What Backspace does at <paramref name="caret"/> with hidden marks: remove <c>Remove</c> when it is set,
    /// otherwise move the caret to <c>Caret</c> (past hidden marks) and delete the character before it as usual.
    /// </summary>
    public (int Caret, (int Start, int Length)? Remove) Backspace(int caret)
    {
        var line = LineOf(caret);
        var lineStart = _lineStarts[line];
        if (caret == lineStart)
        {
            return (caret, null);
        }

        var prose = ProseStart(caret);
        if (caret == prose && prose > lineStart)
        {
            // At the start of the text: a heading, quote or list item loses its prefix; otherwise join the lines.
            var prefix = StructurePrefix().Match(_text, lineStart, _lineEnds[line] - lineStart);
            return prefix.Success && prefix.Groups["marker"].Length > 0
                ? (caret, (prefix.Groups["marker"].Index, prefix.Groups["marker"].Length))
                : (lineStart, null);
        }

        var target = caret;
        while (HiddenSpans(line).FirstOrDefault(s => s.End == target && s.Start < target) is { Length: > 0 } mark)
        {
            target = mark.Start;
        }

        if (target > lineStart && WholeRun(target - 1, target) is { } run)
        {
            return (caret, run);
        }

        return (target, null);
    }

    /// <summary>What Delete does at <paramref name="caret"/> with hidden marks (see <see cref="Backspace"/>).</summary>
    public (int Caret, (int Start, int Length)? Remove) Delete(int caret)
    {
        var line = LineOf(caret);
        var target = caret;
        while (HiddenSpans(line).FirstOrDefault(s => s.Start == target && s.End > target) is { Length: > 0 } mark)
        {
            target = mark.End;
        }

        if (target < _lineEnds[line] && WholeRun(target, target + 1) is { } run)
        {
            return (caret, run);
        }

        return (target, null);
    }

    /// <summary>
    /// Prepares Enter at <paramref name="caret"/>: the caret moves to <c>Caret</c>, <c>Insert</c> goes in there,
    /// and the line break goes <c>CaretInInsert</c> characters into it. At the end of formatted text the caret
    /// moves past the closing marks, at its start before the opening ones; inside it, the marks are closed before
    /// the break and reopened after it.
    /// </summary>
    public (int Caret, string Insert, int CaretInInsert) Enter(int caret)
    {
        var runs = RunsAround(caret).ToList();
        if (runs.Count == 0)
        {
            return (caret, "", 0);
        }

        var line = LineOf(caret);
        var inside = runs.Where(r => r.Content.Start < caret && caret < r.Content.End).OrderBy(r => r.Content.Length).ToList();
        if (inside.Count > 0)
        {
            var close = string.Concat(inside.Select(r => _text[r.Close.Start..r.Close.End]));
            var open = string.Concat(Enumerable.Reverse(inside).Select(r => _text[r.Open.Start..r.Open.End]));
            return (caret, close + open, close.Length);
        }

        var target = caret;
        if (runs.Any(r => r.Content.End == caret))
        {
            while (HiddenSpans(line).FirstOrDefault(s => s.Start == target && IsClosingMark(s)) is { Length: > 0 } mark)
            {
                target = mark.End;
            }
        }
        else
        {
            while (HiddenSpans(line).FirstOrDefault(s => s.End == target && IsOpeningMark(s)) is { Length: > 0 } mark)
            {
                target = mark.Start;
            }
        }

        return (target, "", 0);
    }

    private List<LiveSpan> HiddenSpans(int line) => [.. (_lines[line] ?? None).Where(HidesAtCaret)];

    /// <summary>A mark whose reveal range spans lines: code fences and setext heading underlines.</summary>
    private bool IsBlockMarker(LiveSpan span) => span.Style == LiveStyle.Marker && LineOf(span.RevealStart) != LineOf(span.RevealEnd);

    private bool IsClosingMark(LiveSpan mark) => (_lines[LineOf(mark.Start)] ?? None).Any(s => RunStyles.Contains(s.Style) && s.End == mark.Start && s.Length > 0);

    private bool IsOpeningMark(LiveSpan mark) => (_lines[LineOf(mark.Start)] ?? None).Any(s => RunStyles.Contains(s.Style) && s.Start == mark.End && s.Length > 0);

    /// <summary>Formatted runs (content with hidden opening and closing marks) whose content touches <paramref name="offset"/>.</summary>
    private IEnumerable<(LiveSpan Open, LiveSpan Content, LiveSpan Close)> RunsAround(int offset)
    {
        var line = LineOf(offset);
        var hidden = HiddenSpans(line);
        foreach (var content in (_lines[line] ?? None).Where(s => RunStyles.Contains(s.Style) && s.Start <= offset && offset <= s.End))
        {
            var open = hidden.FirstOrDefault(s => s.End == content.Start);
            var close = hidden.FirstOrDefault(s => s.Start == content.End);
            if (open.Length > 0 && close.Length > 0)
            {
                yield return (open, content, close);
            }
        }
    }

    /// <summary>The whole run (marks included) when [<paramref name="start"/>, <paramref name="end"/>) is all of its content.</summary>
    private (int Start, int Length)? WholeRun(int start, int end) =>
        RunsAround(start).FirstOrDefault(r => r.Content.Start == start && r.Content.End == end) is { Content.Length: > 0 } run
            ? (run.Open.Start, run.Close.End - run.Open.Start)
            : null;

    // A heading, quote or list prefix; "marker" excludes the indentation before it.
    [GeneratedRegex(@"^[ \t]*(?<marker>#{1,6}[ \t]+|(?:>[ \t]?)+|(?:[-*+]|\d{1,9}[.)])[ \t]+(?:\[[ xX]\][ \t]+)?)")]
    private static partial Regex StructurePrefix();
}
