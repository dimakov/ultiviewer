namespace Ultiviewer.Core.Markdown;

public enum TableAlignment
{
    None,
    Left,
    Center,
    Right,
}

/// <summary>A table cell: its trimmed Markdown and where that text sits in the source (absolute offsets).</summary>
public sealed record MarkdownTableCell(string Text, int Start, int Length);

/// <summary>A GitHub-style pipe table read from Markdown source, with source positions for every cell.</summary>
public sealed class MarkdownTable
{
    public required IReadOnlyList<MarkdownTableCell> Header { get; init; }

    public required IReadOnlyList<TableAlignment> Alignments { get; init; }

    public required IReadOnlyList<IReadOnlyList<MarkdownTableCell>> Rows { get; init; }

    public int ColumnCount => Alignments.Count;

    /// <summary>
    /// Reads the table whose lines lie in <paramref name="text"/>[<paramref name="start"/>..<paramref name="end"/>).
    /// Returns null unless the second line is a delimiter row (<c>|---|:--:|</c>).
    /// </summary>
    public static MarkdownTable? Parse(string text, int start, int end)
    {
        var lines = new List<(int Start, int End)>();
        var position = start;
        while (position < end)
        {
            var lineEnd = text.IndexOfAny(['\r', '\n'], position, end - position);
            lineEnd = lineEnd < 0 ? end : lineEnd;
            lines.Add((position, lineEnd));
            position = lineEnd + (lineEnd < text.Length - 1 && text[lineEnd] == '\r' && text[lineEnd + 1] == '\n' ? 2 : 1);
        }

        if (lines.Count < 2)
        {
            return null;
        }

        var delimiter = SplitRow(text, lines[1].Start, lines[1].End);
        var alignments = new List<TableAlignment>();
        foreach (var cell in delimiter)
        {
            var spec = cell.Text;
            if (spec.Length == 0 || spec.Trim(':').Length == 0 || spec.Trim(':').Any(c => c != '-'))
            {
                return null;
            }

            alignments.Add((spec.StartsWith(':'), spec.EndsWith(':')) switch
            {
                (true, true) => TableAlignment.Center,
                (true, false) => TableAlignment.Left,
                (false, true) => TableAlignment.Right,
                _ => TableAlignment.None,
            });
        }

        return new MarkdownTable
        {
            Header = SplitRow(text, lines[0].Start, lines[0].End),
            Alignments = alignments,
            Rows = lines.Skip(2).Select(l => SplitRow(text, l.Start, l.End)).ToList(),
        };
    }

    /// <summary>
    /// Splits one table row into cells. Leading and trailing pipes are optional; <c>\|</c> and pipes inside
    /// backtick code spans do not split.
    /// </summary>
    public static IReadOnlyList<MarkdownTableCell> SplitRow(string text, int lineStart, int lineEnd)
    {
        var first = lineStart;
        while (first < lineEnd && text[first] is ' ' or '\t')
        {
            first++;
        }

        var last = lineEnd;
        while (last > first && text[last - 1] is ' ' or '\t')
        {
            last--;
        }

        if (first < last && text[first] == '|')
        {
            first++;
        }

        var bounds = new List<(int Start, int End)>();
        var cellStart = first;
        var i = first;
        while (i < last)
        {
            var c = text[i];
            if (c == '\\')
            {
                i += 2;
                continue;
            }

            if (c == '`')
            {
                var run = 0;
                while (i + run < last && text[i + run] == '`')
                {
                    run++;
                }

                var close = text.IndexOf(new string('`', run), i + run, last - i - run, StringComparison.Ordinal);
                i = close < 0 ? i + run : close + run;
                continue;
            }

            if (c == '|')
            {
                bounds.Add((cellStart, i));
                cellStart = i + 1;
            }

            i++;
        }

        // A trailing pipe closes the last cell; without one the rest of the line is a cell.
        if (cellStart < last || bounds.Count == 0)
        {
            bounds.Add((cellStart, last));
        }

        var cells = new List<MarkdownTableCell>(bounds.Count);
        foreach (var (start, end) in bounds)
        {
            var s = start;
            var e = end;
            while (s < e && text[s] is ' ' or '\t')
            {
                s++;
            }

            while (e > s && text[e - 1] is ' ' or '\t')
            {
                e--;
            }

            cells.Add(new MarkdownTableCell(text[s..e], s, e - s));
        }

        return cells;
    }
}
