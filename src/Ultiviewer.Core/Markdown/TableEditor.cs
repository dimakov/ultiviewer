using System.Globalization;
using System.Text;
using Ultiviewer.Core.Notebooks;

namespace Ultiviewer.Core.Markdown;

public enum TableAction
{
    NextCell,
    PreviousCell,
    NextRow,
    InsertRowAbove,
    InsertRowBelow,
    DeleteRow,
    InsertColumnLeft,
    InsertColumnRight,
    DeleteColumn,
    MoveRowUp,
    MoveRowDown,
    MoveColumnLeft,
    MoveColumnRight,
    AlignLeft,
    AlignCenter,
    AlignRight,
    AlignNone,
    SortAscending,
    SortDescending,
    Format,
}

/// <summary>A pipe table found around the caret: its source range, cells, and the caret's cell (row 0 = header).</summary>
public sealed class TableAt
{
    public required int Start { get; init; }

    public required int End { get; init; }

    public required List<List<string>> Rows { get; init; }

    public required List<TableAlignment> Alignments { get; init; }

    public required int Row { get; init; }

    public required int Column { get; init; }

    public int ColumnCount => Alignments.Count;
}

/// <summary>
/// Spreadsheet-like editing of Markdown pipe tables (in the spirit of Obsidian's Advanced Tables): every action
/// rewrites the whole table with aligned columns and returns one <see cref="TextEdit"/> with the new caret or
/// cell selection. Also turns tab-separated text (copied from Excel) into a table.
/// </summary>
public static class TableEditor
{
    /// <summary>The table whose lines contain <paramref name="caret"/>, outside fenced code; null otherwise.</summary>
    public static TableAt? Find(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var lines = Lines(text);
        var current = lines.FindLastIndex(l => l.Start <= caret);
        if (current < 0 || !IsRow(text, lines[current]) || InsideFence(text, lines, current))
        {
            return null;
        }

        var first = current;
        while (first > 0 && IsRow(text, lines[first - 1]))
        {
            first--;
        }

        var last = current;
        while (last < lines.Count - 1 && IsRow(text, lines[last + 1]))
        {
            last++;
        }

        // A table starts with header + delimiter; text above it (another block's rows) is not part of it.
        for (var start = first; start < last; start++)
        {
            var table = MarkdownTable.Parse(text, lines[start].Start, lines[last].End);
            if (table is null)
            {
                continue;
            }

            if (current < start)
            {
                return null;
            }

            var columns = table.ColumnCount;
            var rows = new List<List<string>> { Normalize(table.Header, columns) };
            rows.AddRange(table.Rows.Select(r => Normalize(r, columns)));
            var lineIndex = current - start;
            return new TableAt
            {
                Start = lines[start].Start,
                End = lines[last].End,
                Rows = rows,
                Alignments = [.. table.Alignments],
                Row = lineIndex <= 1 ? 0 : lineIndex - 1,
                Column = Math.Clamp(ColumnAt(text, lines[current], caret), 0, columns - 1),
            };
        }

        return null;
    }

    /// <summary>Applies <paramref name="action"/> to the table around the caret; null when the caret is not in a table.</summary>
    public static TextEdit? Apply(string text, int caret, TableAction action, string newLine)
    {
        if (Find(text, caret) is not { } table)
        {
            return null;
        }

        var rows = table.Rows;
        var alignments = table.Alignments;
        var row = table.Row;
        var column = table.Column;
        var lastRow = rows.Count - 1;
        var select = false;
        var exit = false;

        void AddRow(int at) => rows.Insert(at, [.. Enumerable.Repeat("", alignments.Count)]);

        switch (action)
        {
            case TableAction.NextCell:
                select = true;
                if (column < alignments.Count - 1)
                {
                    column++;
                }
                else
                {
                    column = 0;
                    if (++row > lastRow)
                    {
                        AddRow(row);
                    }
                }

                break;
            case TableAction.PreviousCell:
                select = true;
                if (column > 0)
                {
                    column--;
                }
                else if (row > 0)
                {
                    row--;
                    column = alignments.Count - 1;
                }

                break;
            case TableAction.NextRow:
                if (row == lastRow && row > 0 && rows[row].All(string.IsNullOrWhiteSpace))
                {
                    rows.RemoveAt(row); // Enter on an empty last row leaves the table
                    exit = true;
                }
                else if (++row > lastRow)
                {
                    AddRow(row);
                }

                break;
            case TableAction.InsertRowAbove:
                row = Math.Max(1, row);
                AddRow(row);
                break;
            case TableAction.InsertRowBelow:
                AddRow(++row);
                break;
            case TableAction.DeleteRow when row > 0:
                rows.RemoveAt(row);
                row = Math.Min(row, rows.Count - 1);
                break;
            case TableAction.MoveRowUp when row > 1:
                (rows[row - 1], rows[row]) = (rows[row], rows[row - 1]);
                row--;
                break;
            case TableAction.MoveRowDown when row > 0 && row < lastRow:
                (rows[row + 1], rows[row]) = (rows[row], rows[row + 1]);
                row++;
                break;
            case TableAction.InsertColumnLeft or TableAction.InsertColumnRight:
                if (action == TableAction.InsertColumnRight)
                {
                    column++;
                }

                foreach (var cells in rows)
                {
                    cells.Insert(column, "");
                }

                alignments.Insert(column, TableAlignment.None);
                break;
            case TableAction.DeleteColumn when alignments.Count > 1:
                foreach (var cells in rows)
                {
                    cells.RemoveAt(column);
                }

                alignments.RemoveAt(column);
                column = Math.Min(column, alignments.Count - 1);
                break;
            case TableAction.MoveColumnLeft when column > 0:
                SwapColumns(rows, alignments, column, column - 1);
                column--;
                break;
            case TableAction.MoveColumnRight when column < alignments.Count - 1:
                SwapColumns(rows, alignments, column, column + 1);
                column++;
                break;
            case TableAction.AlignLeft:
                alignments[column] = TableAlignment.Left;
                break;
            case TableAction.AlignCenter:
                alignments[column] = TableAlignment.Center;
                break;
            case TableAction.AlignRight:
                alignments[column] = TableAlignment.Right;
                break;
            case TableAction.AlignNone:
                alignments[column] = TableAlignment.None;
                break;
            case TableAction.SortAscending or TableAction.SortDescending:
                var body = rows.Skip(1).OrderBy(r => r[column], CellComparer.Instance).ToList();
                if (action == TableAction.SortDescending)
                {
                    body.Reverse();
                }

                rows.RemoveRange(1, rows.Count - 1);
                rows.AddRange(body);
                break;
        }

        var formatted = Format(rows, alignments, newLine, out var cellRanges);
        if (exit)
        {
            // Continue on a new line after a blank one: text right under a table would become another row.
            var replacement = formatted + newLine + newLine;
            return new TextEdit(table.Start, table.End - table.Start, replacement, table.Start + replacement.Length, 0);
        }

        var (start, length) = cellRanges[Math.Clamp(row, 0, rows.Count - 1)][Math.Clamp(column, 0, alignments.Count - 1)];
        return select
            ? new TextEdit(table.Start, table.End - table.Start, formatted, table.Start + start, length)
            : new TextEdit(table.Start, table.End - table.Start, formatted, table.Start + start + length, 0);
    }

    /// <summary>
    /// Writes a table with padded, aligned columns. <paramref name="cells"/> receives each cell's text range
    /// (relative to the start), indexed [row][column] with row 0 the header.
    /// </summary>
    public static string Format(IReadOnlyList<IReadOnlyList<string>> rows, IReadOnlyList<TableAlignment> alignments, string newLine, out List<List<(int Start, int Length)>> cells)
    {
        var columns = alignments.Count;
        var widths = new int[columns];
        for (var c = 0; c < columns; c++)
        {
            widths[c] = Math.Max(3, rows.Max(r => c < r.Count ? Width(r[c].Trim()) : 0));
        }

        var builder = new StringBuilder();
        cells = [];
        for (var r = 0; r < rows.Count; r++)
        {
            var positions = new List<(int, int)>();
            builder.Append('|');
            for (var c = 0; c < columns; c++)
            {
                var text = c < rows[r].Count ? rows[r][c].Trim() : "";
                var pad = widths[c] - Width(text);
                var (left, right) = alignments[c] switch
                {
                    TableAlignment.Right => (pad, 0),
                    TableAlignment.Center => (pad / 2, pad - pad / 2),
                    _ => (0, pad),
                };
                builder.Append(' ').Append(' ', left);
                positions.Add((builder.Length, text.Length));
                builder.Append(text).Append(' ', right).Append(" |");
            }

            cells.Add(positions);
            if (r == 0)
            {
                builder.Append(newLine).Append('|');
                for (var c = 0; c < columns; c++)
                {
                    var dashes = new string('-', widths[c] + 2 - (alignments[c] == TableAlignment.Center ? 2 : alignments[c] is TableAlignment.Left or TableAlignment.Right ? 1 : 0));
                    builder.Append(alignments[c] switch
                    {
                        TableAlignment.Left => ":" + dashes,
                        TableAlignment.Right => dashes + ":",
                        TableAlignment.Center => ":" + dashes + ":",
                        _ => dashes,
                    }).Append('|');
                }
            }

            if (r < rows.Count - 1)
            {
                builder.Append(newLine);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// A Markdown table for tab-separated text (Excel and Sheets copy cells this way): at least two columns,
    /// every row with the same number of cells, the first row as header. Null when the text isn't like that.
    /// </summary>
    public static string? FromTsv(string text, string newLine)
    {
        if (ParseTsv(text) is not { } rows)
        {
            return null;
        }

        var columns = rows[0].Count;
        var alignments = Enumerable.Range(0, columns)
            .Select(c => rows.Count > 1 && rows.Skip(1).All(r => r[c].Length == 0 || IsNumber(r[c])) && rows.Skip(1).Any(r => r[c].Length > 0)
                ? TableAlignment.Right
                : TableAlignment.None)
            .ToList();
        return Format(rows, alignments, newLine, out _);
    }

    /// <summary>Rows of a TSV text, with Excel's quoting ("a""b", cells with tabs or line breaks); null unless rectangular with 2+ columns.</summary>
    public static List<List<string>>? ParseTsv(string text)
    {
        if (!text.Contains('\t'))
        {
            return null;
        }

        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var i = 0;
        var quoted = false;
        while (i < text.Length)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i += 2;
                    continue;
                }

                if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(c);
                }

                i++;
                continue;
            }

            if (c == '"' && cell.Length == 0)
            {
                quoted = true;
            }
            else if (c == '\t')
            {
                row.Add(Cell(cell));
            }
            else if (c is '\r' or '\n')
            {
                row.Add(Cell(cell));
                rows.Add(row);
                row = [];
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
            }
            else
            {
                cell.Append(c);
            }

            i++;
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(Cell(cell));
            rows.Add(row);
        }

        var single = rows.Count == 1 && (text.EndsWith('\n') || text.EndsWith('\r'));
        if (rows.Count == 0 || rows[0].Count < 2 || rows.Any(r => r.Count != rows[0].Count) || (rows.Count < 2 && !single))
        {
            return null;
        }

        return rows;
    }

    /// <summary>Inserts rows (for example pasted from Excel) below the caret's row, widening the table if needed.</summary>
    public static TextEdit? InsertRows(string text, int caret, IReadOnlyList<IReadOnlyList<string>> newRows, string newLine)
    {
        if (Find(text, caret) is not { } table || newRows.Count == 0)
        {
            return null;
        }

        var columns = Math.Max(table.ColumnCount, newRows.Max(r => r.Count));
        while (table.Alignments.Count < columns)
        {
            table.Alignments.Add(TableAlignment.None);
        }

        var rows = table.Rows.Select(r => Normalize(r, columns)).ToList();
        var at = table.Row + 1;
        rows.InsertRange(at, newRows.Select(r => Normalize(r, columns)));
        var formatted = Format(rows, table.Alignments, newLine, out var cells);
        var lastInserted = cells[at + newRows.Count - 1][^1];
        return new TextEdit(table.Start, table.End - table.Start, formatted, table.Start + lastInserted.Start + lastInserted.Length, 0);
    }

    /// <summary>Monospace columns: wide East Asian characters and emoji take two cells, combining marks none.</summary>
    public static int Width(string text)
    {
        var width = 0;
        foreach (var c in text)
        {
            if (char.IsLowSurrogate(c))
            {
                continue;
            }

            width += char.IsHighSurrogate(c) ? 2
                : CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format ? 0
                : c is >= 'ᄀ' and <= 'ᅟ' or >= '⺀' and <= '꓏' or >= '가' and <= '힣' or >= '豈' and <= '﫿'
                    or >= '︰' and <= '﹏' or >= '＀' and <= '｠' or >= '￠' and <= '￦' ? 2
                : 1;
        }

        return width;
    }

    private static string Cell(StringBuilder cell)
    {
        var text = cell.ToString().Trim().Replace("\r\n", "<br>", StringComparison.Ordinal).Replace("\n", "<br>", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal);
        cell.Clear();
        return text;
    }

    private static bool IsNumber(string text) =>
        double.TryParse(text.Replace("%", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal).TrimStart('$', '€', '£', '₪'),
            NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    private static List<string> Normalize(IEnumerable<MarkdownTableCell> cells, int columns) => Normalize(cells.Select(c => c.Text).ToList(), columns);

    private static List<string> Normalize(IReadOnlyList<string> cells, int columns) =>
        [.. Enumerable.Range(0, columns).Select(i => i < cells.Count ? cells[i] : "")];

    private static void SwapColumns(List<List<string>> rows, List<TableAlignment> alignments, int a, int b)
    {
        foreach (var cells in rows)
        {
            (cells[a], cells[b]) = (cells[b], cells[a]);
        }

        (alignments[a], alignments[b]) = (alignments[b], alignments[a]);
    }

    private static List<(int Start, int End)> Lines(string text)
    {
        var lines = new List<(int, int)>();
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || text[i] is '\r' or '\n')
            {
                lines.Add((start, i));
                if (i < text.Length && text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                start = i + 1;
            }
        }

        return lines;
    }

    private static bool IsRow(string text, (int Start, int End) line)
    {
        var content = text.AsSpan(line.Start, line.End - line.Start).Trim();
        return content.Length > 0 && content.Contains('|');
    }

    /// <summary>True when the caret's line is inside a fenced code block (pasted cells stay text there).</summary>
    public static bool IsInsideFence(string text, int caret)
    {
        var lines = Lines(text);
        return InsideFence(text, lines, Math.Max(0, lines.FindLastIndex(l => l.Start <= Math.Clamp(caret, 0, text.Length))));
    }

    private static bool InsideFence(string text, List<(int Start, int End)> lines, int index)
    {
        var inside = false;
        for (var i = 0; i < index; i++)
        {
            var content = text.AsSpan(lines[i].Start, lines[i].End - lines[i].Start).TrimStart();
            if (content.StartsWith("```", StringComparison.Ordinal) || content.StartsWith("~~~", StringComparison.Ordinal))
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>The cell index under the caret: unescaped pipes before it (outside code spans), minus a leading pipe.</summary>
    private static int ColumnAt(string text, (int Start, int End) line, int caret)
    {
        var pipes = 0;
        var leading = false;
        var seenContent = false;
        for (var i = line.Start; i < caret && i < line.End; i++)
        {
            var c = text[i];
            if (c == '\\')
            {
                i++;
                seenContent = true;
                continue;
            }

            if (c == '`')
            {
                var close = text.IndexOf('`', i + 1, line.End - i - 1);
                if (close >= 0 && close < caret)
                {
                    i = close;
                    seenContent = true;
                    continue;
                }
            }

            if (c == '|')
            {
                leading |= !seenContent;
                pipes++;
            }
            else if (c is not (' ' or '\t'))
            {
                seenContent = true;
            }
        }

        return pipes - (leading ? 1 : 0);
    }

    /// <summary>Numbers by value, everything else naturally ("Item 2" before "Item 10"), empty cells last.</summary>
    private sealed class CellComparer : IComparer<string>
    {
        public static readonly CellComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            x ??= "";
            y ??= "";
            if (x.Length == 0 || y.Length == 0)
            {
                return (x.Length == 0).CompareTo(y.Length == 0);
            }

            if (IsNumber(x) && IsNumber(y))
            {
                return Number(x).CompareTo(Number(y));
            }

            return NaturalStringComparer.Instance.Compare(x, y);
        }

        private static double Number(string text) =>
            double.Parse(text.Replace("%", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal).TrimStart('$', '€', '£', '₪'), NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
