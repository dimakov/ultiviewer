using System.Text.RegularExpressions;

namespace Ultiviewer.Core.Text;

/// <summary>A document's text direction setting: decided from the text, or fixed.</summary>
public enum TextDirectionMode
{
    Auto,
    LeftToRight,
    RightToLeft,
}

/// <summary>
/// Right-to-left (Hebrew, Arabic…) text detection. A line's direction comes from its first strong letter after any
/// Markdown prefix (<c>#</c>, list markers, <c>- [x]</c>, <c>&gt;</c>), as browsers do for <c>dir="auto"</c>. A
/// document is right-to-left when at least as many of its prose lines are (front matter, code blocks, inline code
/// and link targets don't count).
/// </summary>
public static partial class BidiText
{
    /// <summary>Hebrew, Arabic, Syriac, Thaana, N'Ko, Samaritan, Mandaic and their presentation forms.</summary>
    public static bool IsRightToLeft(char c) =>
        c is >= '\u0590' and <= '\u08FF'
        || c is >= '\uFB1D' and <= '\uFDFF'
        || c is >= '\uFE70' and <= '\uFEFF';

    /// <summary>A letter that sets left-to-right direction (Latin, Greek, Cyrillic, CJK…).</summary>
    public static bool IsLeftToRight(char c) => char.IsLetter(c) && !IsRightToLeft(c);

    /// <summary>True for right-to-left, false for left-to-right, null when the text has no strong letter.</summary>
    public static bool? FirstStrong(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (IsRightToLeft(c))
            {
                return true;
            }

            if (IsLeftToRight(c))
            {
                return false;
            }
        }

        return null;
    }

    /// <summary>Where a line's prose starts: after its Markdown prefix (heading marks, quote, list marker, task box).</summary>
    public static int ProseStart(string line) => MarkdownPrefix().Match(line) is { Success: true } prefix ? prefix.Length : 0;

    /// <summary>A Markdown line's own direction (its prefix and link targets skipped), or null without letters.</summary>
    public static bool? LineDirection(string line)
    {
        var text = line.AsSpan(ProseStart(line));
        if (!text.Contains("](", StringComparison.Ordinal) && !text.Contains('`'))
        {
            return FirstStrong(text);
        }

        return FirstStrong(Ignored().Replace(text.ToString(), " "));
    }

    /// <summary>Whether a document reads right to left; null when it has no letters to decide by.</summary>
    public static bool? Detect(string text, int maxLines = 2000)
    {
        int rtl = 0, ltr = 0;
        bool? first = null;
        var inFence = false;
        var fence = "";
        var lines = 0;
        var position = 0;
        var frontMatter = text.StartsWith("---", StringComparison.Ordinal);
        while (position <= text.Length && lines++ < maxLines)
        {
            var end = text.IndexOf('\n', position);
            var line = (end < 0 ? text[position..] : text[position..end]).TrimEnd('\r');
            position = end < 0 ? text.Length + 1 : end + 1;
            if (frontMatter)
            {
                frontMatter = lines == 1 || line is not ("---" or "...");
                continue;
            }

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                if (!inFence)
                {
                    inFence = true;
                    fence = trimmed[..3];
                }
                else if (trimmed.StartsWith(fence, StringComparison.Ordinal))
                {
                    inFence = false;
                }

                continue;
            }

            if (inFence || line.StartsWith("    ", StringComparison.Ordinal) && !MarkdownPrefix().IsMatch(trimmed))
            {
                continue;
            }

            switch (LineDirection(line))
            {
                case true:
                    rtl++;
                    first ??= true;
                    break;
                case false:
                    ltr++;
                    first ??= false;
                    break;
            }
        }

        return rtl == ltr ? first : rtl > ltr;
    }

    [GeneratedRegex(@"^[ \t]*(?:(?:#{1,6}|>|[-+*]|\d+[.)])[ \t]+)*(?:\[[ xX]\][ \t]+)?")]
    private static partial Regex MarkdownPrefix();

    // Link targets, inline code and HTML tags hold no prose.
    [GeneratedRegex(@"\]\([^)]*\)|`[^`]*`|<[^>]+>")]
    private static partial Regex Ignored();
}
