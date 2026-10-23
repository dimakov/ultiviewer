using System.Text;
using Ultiviewer.Core.Links;

namespace Ultiviewer.Core.Search;

/// <summary>A unit of indexed text and the 1-based line it starts on.</summary>
public readonly record struct TextBlock(int Line, string Text);

/// <summary>
/// Splits Markdown into the blocks the index stores (OneNote indexes paragraphs, not whole pages): headings,
/// paragraphs, single list items, whole fenced code blocks and whole tables. Line-based on purpose — it only
/// needs to be good enough to propose candidates; exact matches are found on the real text.
/// </summary>
public static class MarkdownBlocks
{
    private const int MaxBlockLength = 4000;

    public static IReadOnlyList<TextBlock> Split(string text)
    {
        var blocks = new List<TextBlock>();
        var lines = text.Split('\n');
        var current = new StringBuilder();
        var start = 0;
        var inFence = false;
        var fenceChar = '\0';
        var inTable = false;

        void Flush()
        {
            if (current.Length > 0)
            {
                var content = current.ToString().Trim();
                for (var i = 0; i < content.Length; i += MaxBlockLength)
                {
                    blocks.Add(new TextBlock(start, content.Substring(i, Math.Min(MaxBlockLength, content.Length - i))));
                }

                current.Clear();
            }

            inTable = false;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var number = i + 1;

            if (CodeRanges.TryReadFence(line, out var ch, out _))
            {
                if (!inFence)
                {
                    Flush();
                    inFence = true;
                    fenceChar = ch;
                    start = number;
                    current.AppendLine(line);
                    continue;
                }

                if (ch == fenceChar)
                {
                    current.AppendLine(line);
                    inFence = false;
                    Flush();
                    continue;
                }
            }

            if (inFence)
            {
                current.AppendLine(line);
                continue;
            }

            var trimmed = line.TrimStart();
            if (trimmed.Length == 0)
            {
                Flush();
                continue;
            }

            var isHeading = trimmed.StartsWith('#') && (trimmed.TrimStart('#').StartsWith(' ') || trimmed.TrimStart('#').Length == 0);
            var isListItem = IsListItem(trimmed);
            var isTableRow = trimmed.StartsWith('|');

            if (isHeading || isListItem || (isTableRow && !inTable) || (!isTableRow && inTable))
            {
                Flush();
            }

            if (current.Length == 0)
            {
                start = number;
            }

            current.AppendLine(line);
            inTable = isTableRow;
            if (isHeading)
            {
                Flush();
            }
        }

        Flush();
        return blocks;
    }

    private static bool IsListItem(string trimmed)
    {
        if (trimmed.Length >= 2 && trimmed[0] is '-' or '*' or '+' && trimmed[1] is ' ' or '\t')
        {
            return true;
        }

        var digits = 0;
        while (digits < trimmed.Length && char.IsAsciiDigit(trimmed[digits]))
        {
            digits++;
        }

        return digits > 0 && digits + 1 < trimmed.Length && trimmed[digits] is '.' or ')' && trimmed[digits + 1] is ' ' or '\t';
    }
}
