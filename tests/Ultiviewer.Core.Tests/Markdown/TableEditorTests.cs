using Ultiviewer.Core.Markdown;

namespace Ultiviewer.Core.Tests.Markdown;

public class TableEditorTests
{
    private const string Table = "intro\n\n| Name | Qty |\n|---|--:|\n| apple | 3 |\n| pear | 12 |\n\nafter";

    /// <summary>Applies an action with the caret at <paramref name="marker"/> and returns the text plus the selected/caret text.</summary>
    private static (string Text, string Selected, int Caret) Run(string text, string marker, TableAction action, int delta = 0)
    {
        var edit = TableEditor.Apply(text, text.IndexOf(marker, StringComparison.Ordinal) + delta, action, "\n")!.Value;
        var result = text[..edit.Start] + edit.Replacement + text[(edit.Start + edit.Length)..];
        return (result, result.Substring(edit.SelectionStart, edit.SelectionLength), edit.SelectionStart);
    }

    [Fact]
    public void FindsTheTableAndTheCaretCell()
    {
        var table = TableEditor.Find(Table, Table.IndexOf("12", StringComparison.Ordinal))!;

        Assert.Equal((2, 1), (table.Row, table.Column));
        Assert.Equal([TableAlignment.None, TableAlignment.Right], table.Alignments);
        Assert.Equal(["pear", "12"], table.Rows[2]);
        Assert.Null(TableEditor.Find(Table, 2));
        Assert.Null(TableEditor.Find("```\n| a | b |\n|---|---|\n```", 4));
    }

    [Fact]
    public void FormatsWithAlignedColumns()
    {
        var (text, _, _) = Run(Table, "apple", TableAction.Format);

        Assert.Equal("intro\n\n| Name  | Qty |\n|-------|----:|\n| apple |   3 |\n| pear  |  12 |\n\nafter", text);
    }

    [Fact]
    public void TabMovesThroughCellsAndAddsARowAtTheEnd()
    {
        var (_, selected, _) = Run(Table, "apple", TableAction.NextCell);
        Assert.Equal("3", selected);

        (_, selected, _) = Run(Table, "3 |", TableAction.NextCell);
        Assert.Equal("pear", selected);

        (_, selected, _) = Run(Table, "apple", TableAction.PreviousCell);
        Assert.Equal("Qty", selected);

        var (text, _, caret) = Run(Table, "12", TableAction.NextCell);
        Assert.Contains("| pear  |  12 |\n|       |     |\n\nafter", text, StringComparison.Ordinal);
        Assert.Equal(text.IndexOf("|       |", StringComparison.Ordinal) + 2, caret);
    }

    [Fact]
    public void EnterGoesDownAndLeavesTheTableFromAnEmptyLastRow()
    {
        var (down, _, caret) = Run(Table, "apple", TableAction.NextRow);
        Assert.Equal("pear", down[(caret - 4)..caret]);

        var (withRow, _, rowCaret) = Run(Table, "pear", TableAction.NextRow);
        Assert.Equal(withRow.IndexOf("|       |", StringComparison.Ordinal) + 2, rowCaret);

        // Enter again on that empty row: it goes away and the caret continues below a blank line.
        var (left, _, leftCaret) = Run(withRow, "|       |", TableAction.NextRow, 2);
        Assert.Equal(Run(Table, "apple", TableAction.Format).Text.Replace("|\n\nafter", "|\n\n\n\nafter", StringComparison.Ordinal), left);
        Assert.Equal("|\n\n", left[(leftCaret - 3)..leftCaret]);
        Assert.Equal("\n\nafter", left[leftCaret..]);

        // A table at the end of the note gets a blank line to continue on.
        var (end, _, endCaret) = Run("| a |\n|---|\n| |", "| |", TableAction.NextRow, 1);
        Assert.Equal("| a   |\n|-----|\n\n", end);
        Assert.Equal(end.Length, endCaret);
    }

    [Fact]
    public void RowAndColumnOperations()
    {
        Assert.Contains("| pear  |  12 |\n| apple |   3 |", Run(Table, "apple", TableAction.MoveRowDown).Text, StringComparison.Ordinal);
        Assert.Contains("| Qty | Name  |\n|----:|-------|", Run(Table, "Name", TableAction.MoveColumnRight).Text, StringComparison.Ordinal);
        Assert.DoesNotContain("apple", Run(Table, "apple", TableAction.DeleteRow).Text, StringComparison.Ordinal);
        Assert.Contains("| Name  |     | Qty |", Run(Table, "apple", TableAction.InsertColumnRight).Text, StringComparison.Ordinal);
        Assert.Contains("| Qty |\n|----:|", Run(Table, "apple", TableAction.DeleteColumn).Text, StringComparison.Ordinal);
        Assert.Contains("|:-----:|", Run(Table, "apple", TableAction.AlignCenter).Text, StringComparison.Ordinal);
        Assert.Contains("| Name  | Qty |\n|-------|----:|\n|       |     |\n| apple |", Run(Table, "apple", TableAction.InsertRowAbove).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void SortsNumbersByValueAndTextNaturally()
    {
        const string table = "| Item | N |\n|---|---|\n| Item 10 | 9 |\n| Item 2 | 10 |\n| | 1 |";

        Assert.EndsWith("| Item 2  | 10  |\n| Item 10 | 9   |\n|         | 1   |", Run(table, "Item 10", TableAction.SortAscending).Text, StringComparison.Ordinal);
        Assert.EndsWith("|         | 1   |\n| Item 10 | 9   |\n| Item 2  | 10  |", Run(table, "| 9", TableAction.SortAscending, 2).Text, StringComparison.Ordinal);
        Assert.EndsWith("| Item 2  | 10  |\n| Item 10 | 9   |\n|         | 1   |", Run(table, "| 9", TableAction.SortDescending, 2).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertsExcelTsvToATable()
    {
        var table = TableEditor.FromTsv("Name\tPrice\r\nTea\t3.5\r\n\"Big \"\"pot\"\"\r\nset\"\t12\r\n", "\n");

        Assert.Equal(
            """
            | Name             | Price |
            |------------------|------:|
            | Tea              |   3.5 |
            | Big "pot"<br>set |    12 |
            """.ReplaceLineEndings("\n"),
            table);
        Assert.Null(TableEditor.FromTsv("just text", "\n"));
        Assert.Null(TableEditor.FromTsv("a\tb\nc", "\n")); // ragged: probably not cells
        Assert.Null(TableEditor.FromTsv("one\ttwo", "\n")); // a single line without a line break
        Assert.NotNull(TableEditor.FromTsv("one\ttwo\r\n", "\n"));
    }

    [Fact]
    public void InsertsPastedRowsBelowTheCaretRow()
    {
        var edit = TableEditor.InsertRows(Table, Table.IndexOf("apple", StringComparison.Ordinal), [["kiwi", "7", "new"]], "\n")!.Value;
        var text = Table[..edit.Start] + edit.Replacement + Table[(edit.Start + edit.Length)..];

        Assert.Contains("| apple |   3 |     |\n| kiwi  |   7 | new |\n| pear  |  12 |     |", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WideCharactersCountTwice()
    {
        Assert.Equal(4, TableEditor.Width("日本"));
        Assert.Equal(1, TableEditor.Width("e\u0301")); // e + combining acute
        Assert.Equal(4, TableEditor.Width("שלום"));
    }
}
