using System.Text.RegularExpressions;

namespace Ultiviewer.Core.Markdown;

/// <summary>An edit to apply: replace <see cref="Length"/> characters at <see cref="Start"/>, then select a range.</summary>
public readonly record struct TextEdit(int Start, int Length, string Replacement, int SelectionStart, int SelectionLength);

/// <summary>The inline styles of OneNote's "Basic Text" group, expressed in Markdown.</summary>
public enum InlineStyle
{
    Bold,
    Italic,
    Underline,
    Strikethrough,
    Subscript,
    Superscript,
    Highlight,
    Code,
}

public enum ListStyle
{
    Bullet,
    Numbered,
    Quote,
}

/// <summary>
/// OneNote-style formatting commands for Markdown text. Every method is pure: it takes the text and the selection
/// and returns the edit, so the editor applies it as one undo step.
/// </summary>
public static partial class MarkdownFormatting
{
    private static (string Open, string Close) Markers(InlineStyle style) => style switch
    {
        InlineStyle.Bold => ("**", "**"),
        InlineStyle.Italic => ("*", "*"),
        InlineStyle.Underline => ("<u>", "</u>"),
        InlineStyle.Strikethrough => ("~~", "~~"),
        InlineStyle.Subscript => ("~", "~"),
        InlineStyle.Superscript => ("^", "^"),
        InlineStyle.Highlight => ("==", "=="),
        _ => ("`", "`"),
    };

    /// <summary>
    /// Wraps the selection (or the word at the caret) in the style's markers, or removes them when it already
    /// has that style. With nothing to wrap, inserts the markers and puts the caret between them.
    /// </summary>
    public static TextEdit ToggleInline(string text, int selectionStart, int selectionLength, InlineStyle style)
    {
        var (open, close) = Markers(style);
        var (start, length) = selectionLength > 0 ? (selectionStart, selectionLength) : WordAt(text, selectionStart);
        if (length == 0)
        {
            return new TextEdit(start, 0, open + close, start + open.Length, 0);
        }

        // Selection includes the markers: "**bold**" selected → "bold".
        var inner = text.Substring(start, length);
        if (inner.Length > open.Length + close.Length && inner.StartsWith(open, StringComparison.Ordinal) && inner.EndsWith(close, StringComparison.Ordinal)
            && HasStyle(inner, open.Length, inner.Length - close.Length, style))
        {
            var unwrapped = inner[open.Length..^close.Length];
            return new TextEdit(start, length, unwrapped, start, unwrapped.Length);
        }

        // Markers right around the selection: "**[bold]**" → "bold".
        if (HasStyle(text, start, start + length, style))
        {
            var outerStart = start - open.Length;
            return new TextEdit(outerStart, length + open.Length + close.Length, inner, outerStart, inner.Length);
        }

        var wrapped = open + inner + close;
        return new TextEdit(start, length, wrapped, start + open.Length, inner.Length);
    }

    /// <summary>True when the text between <paramref name="start"/> and <paramref name="end"/> has the style's markers around it.</summary>
    private static bool HasStyle(string text, int start, int end, InlineStyle style)
    {
        var (open, close) = Markers(style);
        if (start < open.Length || end + close.Length > text.Length
            || !text.AsSpan(start - open.Length, open.Length).SequenceEqual(open)
            || !text.AsSpan(end, close.Length).SequenceEqual(close))
        {
            return false;
        }

        if (open.Length > 2 || open[0] != open[^1] || (open.Length == 2 && open[0] != open[1]))
        {
            return true; // <u>…</u>, `code`
        }

        // Runs of the same marker character: "*" is italic when the run is 1 or 3 long, "**" bold when 2 or 3.
        var c = open[0];
        var before = 0;
        while (start - before - 1 >= 0 && text[start - before - 1] == c) before++;
        var after = 0;
        while (end + after < text.Length && text[end + after] == c) after++;
        var run = Math.Min(before, after);
        return open.Length == 1 ? run % 2 == 1 : run >= 2;
    }

    /// <summary>
    /// The word around the caret (letters, digits, underscores, apostrophes inside a word). A caret inside an
    /// inline HTML tag such as <c>&lt;/u&gt;</c> counts as just after the tag, so the tag name is never wrapped.
    /// </summary>
    public static (int Start, int Length) WordAt(string text, int caret)
    {
        static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_' || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.NonSpacingMark;
        var start = OutsideTag(text, Math.Clamp(caret, 0, text.Length));
        var end = start;
        while (start > 0 && (IsWord(text[start - 1]) || (text[start - 1] == '\'' && start > 1 && IsWord(text[start - 2])))) start--;
        while (end < text.Length && (IsWord(text[end]) || (text[end] == '\'' && end + 1 < text.Length && IsWord(text[end + 1])))) end++;
        return (start, end - start);
    }

    private static int OutsideTag(string text, int caret)
    {
        var open = caret > 0 ? text.LastIndexOf('<', caret - 1) : -1;
        if (open < 0 || text.IndexOfAny(['>', '\n'], open, caret - open) >= 0)
        {
            return caret;
        }

        var close = text.IndexOfAny(['>', '<', '\n'], caret);
        return close >= 0 && text[close] == '>' && InlineTag().IsMatch(text.AsSpan(open, close - open + 1)) ? close + 1 : caret;
    }

    /// <summary>Makes a line a heading of <paramref name="level"/> (1–6), or normal text with 0.</summary>
    public static string SetHeading(string line, int level)
    {
        var body = HeadingPrefix().Replace(line, "", 1);
        return level <= 0 ? body : new string('#', Math.Min(level, 6)) + " " + body.TrimStart();
    }

    /// <summary>
    /// Toggles bullets, numbering or quotes on lines: if every non-empty line already has that style it is
    /// removed, otherwise it is applied (replacing another list style). Numbering counts from 1.
    /// </summary>
    public static IReadOnlyList<string> ToggleList(IReadOnlyList<string> lines, ListStyle style)
    {
        var nonEmpty = lines.Where(l => l.Trim().Length > 0).ToList();
        var allHave = nonEmpty.Count > 0 && nonEmpty.All(l => style switch
        {
            ListStyle.Bullet => BulletPrefix().IsMatch(l),
            ListStyle.Numbered => NumberPrefix().IsMatch(l),
            _ => QuotePrefix().IsMatch(l),
        });

        // Only empty lines (a new paragraph): they become empty items, ready to type into.
        var onlyEmpty = nonEmpty.Count == 0;
        var number = 0;
        return lines.Select(line =>
        {
            if (line.Trim().Length == 0 && !onlyEmpty)
            {
                return line;
            }

            var indent = line[..(line.Length - line.TrimStart(' ', '\t').Length)];
            var body = line[indent.Length..];
            body = style == ListStyle.Quote ? QuotePrefix().Replace(body, "", 1) : ListMarker().Replace(body, "", 1);
            if (allHave)
            {
                return indent + body;
            }

            return style switch
            {
                ListStyle.Bullet => indent + "- " + body,
                ListStyle.Numbered => indent + (++number) + ". " + body,
                _ => indent + "> " + body,
            };
        }).ToList();
    }

    /// <summary>Removes character formatting (bold, italic, underline, colors…) but keeps the text.</summary>
    public static string ClearFormatting(string text)
    {
        var result = text;
        foreach (var (pattern, replacement) in ClearRules)
        {
            string previous;
            do
            {
                previous = result;
                result = pattern.Replace(result, replacement);
            }
            while (result != previous);
        }

        return result;
    }

    private static readonly (Regex Pattern, string Replacement)[] ClearRules =
    [
        (new Regex(@"<span\b[^>]*>(.*?)</span>", RegexOptions.Singleline), "$1"),
        (new Regex(@"</?(?:u|b|i|em|strong|s|del|ins|mark|sub|sup|font)\b[^>]*>", RegexOptions.IgnoreCase), ""),
        (new Regex(@"\*\*(?=\S)(.+?)(?<=\S)\*\*"), "$1"),
        (new Regex(@"__(?=\S)(.+?)(?<=\S)__"), "$1"),
        (new Regex(@"(?<![\*\w])\*(?=[^\s\*])(.+?)(?<=\S)\*(?![\*\w])"), "$1"),
        (new Regex(@"(?<![_\w])_(?=[^\s_])(.+?)(?<=\S)_(?![_\w])"), "$1"),
        (new Regex(@"~~(?=\S)(.+?)(?<=\S)~~"), "$1"),
        (new Regex(@"==(?=\S)(.+?)(?<=\S)=="), "$1"),
        (new Regex(@"(?<!~)~(?=[^\s~])([^~\s]+?)~(?!~)"), "$1"),
        (new Regex(@"\^(?=\S)([^\^\s]+?)\^"), "$1"),
        (new Regex(@"`([^`\r\n]+)`"), "$1"),
    ];

    /// <summary>Colors the selection (<c>&lt;span style="color:…"&gt;</c>), recolors it, or removes the color with null.</summary>
    public static TextEdit SetColor(string text, int selectionStart, int selectionLength, string? color)
    {
        var (start, length) = selectionLength > 0 ? (selectionStart, selectionLength) : WordAt(text, selectionStart);
        var inner = text.Substring(start, length);
        var existing = ColorSpan().Match(inner);
        if (existing.Success && existing.Index == 0 && existing.Length == inner.Length)
        {
            inner = existing.Groups["text"].Value;
        }

        var replacement = color is null ? inner : $"<span style=\"color:{color}\">{inner}</span>";
        var offset = color is null ? 0 : replacement.IndexOf('>') + 1;
        return new TextEdit(start, length, replacement, start + offset, inner.Length);
    }

    /// <summary>Turns the selection into a link; the URL part is selected so it can be typed over.</summary>
    public static TextEdit MakeLink(string text, int selectionStart, int selectionLength, string? url)
    {
        var (start, length) = selectionLength > 0 ? (selectionStart, selectionLength) : WordAt(text, selectionStart);
        var label = text.Substring(start, length);
        var target = string.IsNullOrWhiteSpace(url) ? "https://" : url.Trim();
        if (label.Length == 0)
        {
            label = string.IsNullOrWhiteSpace(url) ? "link" : target;
        }

        var replacement = $"[{label}]({target})";
        return new TextEdit(start, length, replacement, start + label.Length + 3, target.Length);
    }

    public static string Table(string newLine) =>
        string.Join(newLine, "| Column 1 | Column 2 | Column 3 |", "|---|---|---|", "|  |  |  |", "|  |  |  |") + newLine;

    /// <summary>Wraps lines in a fenced code block.</summary>
    public static string CodeBlock(string lines, string newLine) =>
        "```" + newLine + lines + (lines.EndsWith('\n') || lines.Length == 0 ? "" : newLine) + "```";

    [GeneratedRegex(@"^[ ]{0,3}#{1,6}[ \t]*")]
    private static partial Regex HeadingPrefix();

    [GeneratedRegex(@"^[ \t]*[-*+][ \t]+")]
    private static partial Regex BulletPrefix();

    [GeneratedRegex(@"^[ \t]*\d{1,9}[.)][ \t]+")]
    private static partial Regex NumberPrefix();

    [GeneratedRegex(@"^[ \t]*>[ \t]?")]
    private static partial Regex QuotePrefix();

    [GeneratedRegex(@"^(?:[-*+]|\d{1,9}[.)])[ \t]+")]
    private static partial Regex ListMarker();

    [GeneratedRegex(@"^<span style=""color:[^""]*"">(?<text>.*)</span>$", RegexOptions.Singleline)]
    private static partial Regex ColorSpan();

    [GeneratedRegex(@"^</?[A-Za-z][A-Za-z0-9]*(?:\s[^<>]*)?/?>$")]
    private static partial Regex InlineTag();
}
