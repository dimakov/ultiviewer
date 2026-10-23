using System.Text.RegularExpressions;

namespace Ultiviewer.Core.Links;

/// <summary>
/// Finds Markdown code (fenced blocks and inline code spans) so link and tag scanners can skip it.
/// </summary>
public static partial class CodeRanges
{
    public static IReadOnlyList<Range> Find(string text)
    {
        var ranges = new List<Range>();
        var lineStart = 0;
        int? fenceStart = null;
        var fenceChar = '\0';
        var fenceLength = 0;

        while (lineStart <= text.Length)
        {
            var lineEnd = text.IndexOf('\n', lineStart);
            if (lineEnd < 0) lineEnd = text.Length;
            var line = text.AsSpan(lineStart, lineEnd - lineStart).TrimEnd('\r');

            if (TryReadFence(line, out var ch, out var length))
            {
                if (fenceStart is null)
                {
                    fenceStart = lineStart;
                    fenceChar = ch;
                    fenceLength = length;
                }
                else if (ch == fenceChar && length >= fenceLength && line.Trim().TrimStart(ch).IsEmpty)
                {
                    ranges.Add(new Range(fenceStart.Value, lineEnd));
                    fenceStart = null;
                }
            }
            else if (fenceStart is null)
            {
                foreach (Match match in InlineCode().Matches(text[lineStart..lineEnd]))
                {
                    ranges.Add(new Range(lineStart + match.Index, lineStart + match.Index + match.Length));
                }
            }

            lineStart = lineEnd + 1;
        }

        if (fenceStart is not null)
        {
            ranges.Add(new Range(fenceStart.Value, text.Length));
        }

        return ranges;
    }

    public static bool Contains(IReadOnlyList<Range> ranges, int position)
    {
        foreach (var range in ranges)
        {
            if (position >= range.Start.Value && position < range.End.Value)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True for a line that opens or closes a fenced code block (``` or ~~~, up to 3 spaces indent).</summary>
    public static bool TryReadFence(ReadOnlySpan<char> line, out char fenceChar, out int length)
    {
        fenceChar = '\0';
        length = 0;
        var indent = 0;
        while (indent < line.Length && line[indent] == ' ' && indent < 4) indent++;
        if (indent > 3 || indent >= line.Length || (line[indent] != '`' && line[indent] != '~'))
        {
            return false;
        }

        fenceChar = line[indent];
        var i = indent;
        while (i < line.Length && line[i] == fenceChar) i++;
        length = i - indent;
        if (length < 3)
        {
            return false;
        }

        // A backtick fence's info string can't contain backticks.
        return fenceChar == '~' || line[i..].IndexOf('`') < 0;
    }

    [GeneratedRegex(@"(`+)(?!`).*?(?<!`)\1(?!`)")]
    private static partial Regex InlineCode();
}
