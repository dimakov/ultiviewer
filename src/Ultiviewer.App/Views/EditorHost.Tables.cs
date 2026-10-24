using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Ultiviewer.App.Editor;
using Ultiviewer.Core.Markdown;

namespace Ultiviewer.App.Views;

/// <summary>
/// Spreadsheet-like Markdown tables: Tab / Shift+Tab / Enter move between cells and keep columns aligned, the
/// Table commands edit rows and columns, and tab-separated text pasted from a spreadsheet becomes a table.
/// The table logic is <see cref="TableEditor"/> (Core).
/// </summary>
public partial class EditorHost
{
    private static readonly (RoutedUICommand Command, TableAction Action)[] TableCommands =
    [
        (EditorCommands.TableInsertRowAbove, TableAction.InsertRowAbove),
        (EditorCommands.TableInsertRowBelow, TableAction.InsertRowBelow),
        (EditorCommands.TableDeleteRow, TableAction.DeleteRow),
        (EditorCommands.TableMoveRowUp, TableAction.MoveRowUp),
        (EditorCommands.TableMoveRowDown, TableAction.MoveRowDown),
        (EditorCommands.TableInsertColumnLeft, TableAction.InsertColumnLeft),
        (EditorCommands.TableInsertColumnRight, TableAction.InsertColumnRight),
        (EditorCommands.TableDeleteColumn, TableAction.DeleteColumn),
        (EditorCommands.TableMoveColumnLeft, TableAction.MoveColumnLeft),
        (EditorCommands.TableMoveColumnRight, TableAction.MoveColumnRight),
        (EditorCommands.TableAlignLeft, TableAction.AlignLeft),
        (EditorCommands.TableAlignCenter, TableAction.AlignCenter),
        (EditorCommands.TableAlignRight, TableAction.AlignRight),
        (EditorCommands.TableAlignNone, TableAction.AlignNone),
        (EditorCommands.TableSortAscending, TableAction.SortAscending),
        (EditorCommands.TableSortDescending, TableAction.SortDescending),
        (EditorCommands.TableFormat, TableAction.Format),
    ];

    private const string TableMenuTag = "table-menu";

    private void InitializeTables()
    {
        foreach (var (command, action) in TableCommands)
        {
            Bind(command, (_, _) => ApplyTable(action, quiet: false));
        }

        Bind(EditorCommands.PasteAsText, (_, _) => PasteAsText());
        ContextMenuOpened += AddTableMenu;
    }

    private bool ApplyTable(TableAction action, bool quiet)
    {
        var document = Editor.Document;
        if (TableEditor.Apply(document.Text, Editor.CaretOffset, action, NewLineAt(Editor.TextArea.Caret.Line)) is not { } edit)
        {
            if (!quiet && _vm is not null)
            {
                _vm.StatusMessage = "Put the caret in a table first.";
            }

            return false;
        }

        if (document.GetText(edit.Start, edit.Length) == edit.Replacement)
        {
            Editor.Select(edit.SelectionStart, edit.SelectionLength); // already tidy: just move
            Editor.TextArea.Caret.BringCaretToView();
        }
        else
        {
            Apply(edit);
        }

        return true;
    }

    /// <summary>Tab and Shift+Tab go to the next / previous cell, Enter to the same cell of the next row.</summary>
    private bool HandleTableKey(KeyEventArgs e)
    {
        TableAction? action = (e.Key, Keyboard.Modifiers) switch
        {
            (Key.Tab, ModifierKeys.None) => TableAction.NextCell,
            (Key.Tab, ModifierKeys.Shift) => TableAction.PreviousCell,
            (Key.Enter, ModifierKeys.None) => TableAction.NextRow,
            _ => null,
        };
        if (action is null)
        {
            return false;
        }

        var document = Editor.Document;
        var line = document.GetLineByOffset(Editor.CaretOffset);
        if (Editor.SelectionLength > 0 && document.GetLineByOffset(Editor.SelectionStart + Editor.SelectionLength) != line)
        {
            return false; // a multi-line selection indents as usual
        }

        return document.GetText(line).Contains('|') && ApplyTable(action.Value, quiet: true);
    }

    /// <summary>
    /// Pasting cells copied from Excel or Google Sheets (tab-separated text): inside a table they become rows
    /// below the caret's row, elsewhere a new table. Returns false for any other text.
    /// </summary>
    private bool PasteTable(string text)
    {
        var document = Editor.Document;
        if (TableEditor.ParseTsv(text) is not { } rows || TableEditor.IsInsideFence(document.Text, Editor.CaretOffset))
        {
            return false;
        }

        var newLine = NewLineAt(Editor.TextArea.Caret.Line);
        if (TableEditor.InsertRows(document.Text, Editor.CaretOffset, rows, newLine) is { } intoTable)
        {
            Apply(intoTable);
            return true;
        }

        if (TableEditor.FromTsv(text, newLine) is not { } table)
        {
            return false;
        }

        InsertBlock(table, newLine);
        _vm!.StatusMessage = $"Pasted {rows.Count} rows as a table (Ctrl+Alt+V pastes the plain text)";
        return true;
    }

    /// <summary>
    /// Replaces the selection with Markdown blocks (a table, pasted paragraphs), adding the blank lines they need
    /// so neighbouring text doesn't join them; the caret ends after the block.
    /// </summary>
    private void InsertBlock(string block, string newLine)
    {
        var document = Editor.Document;
        var start = Editor.SelectionStart;
        var end = start + Editor.SelectionLength;
        var startLine = document.GetLineByOffset(start);
        var endLine = document.GetLineByOffset(end);
        var textBefore = document.GetText(startLine.Offset, start - startLine.Offset).Trim().Length > 0;
        var lineAbove = startLine.PreviousLine is { } above && document.GetText(above).Trim().Length > 0;
        var textAfter = document.GetText(end, endLine.EndOffset - end).Trim().Length > 0;
        var lineBelow = endLine.NextLine is { } below && document.GetText(below).Trim().Length > 0;
        var prefix = textBefore ? newLine + newLine : lineAbove && start == startLine.Offset ? newLine : "";
        var suffix = textAfter ? newLine + newLine : lineBelow ? newLine + newLine : newLine;
        var insertion = prefix + block + suffix;
        document.Replace(start, end - start, insertion);
        Editor.Select(start + prefix.Length + block.Length, 0);
    }

    private void PasteAsText()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                Editor.TextArea.Selection.ReplaceSelectionWithText(Clipboard.GetText());
            }
        }
        catch (ExternalException)
        {
        }
    }

    /// <summary>When the caret is in a table, the editor's context menu gets a Table submenu.</summary>
    private void AddTableMenu(ContextMenu menu)
    {
        foreach (var old in menu.Items.OfType<FrameworkElement>().Where(i => Equals(i.Tag, TableMenuTag)).ToList())
        {
            menu.Items.Remove(old);
        }

        if (_current is not { IsMarkdown: true } || TableEditor.Find(Editor.Document.Text, Editor.CaretOffset) is null)
        {
            return;
        }

        var table = new MenuItem { Header = "Table", Tag = TableMenuTag };
        foreach (var (command, action) in TableCommands)
        {
            if (action is TableAction.InsertColumnLeft or TableAction.AlignLeft or TableAction.SortAscending or TableAction.Format)
            {
                table.Items.Add(new Separator());
            }

            table.Items.Add(new MenuItem { Command = command, CommandTarget = Editor.TextArea });
        }

        menu.Items.Insert(0, new Separator { Tag = TableMenuTag });
        menu.Items.Insert(0, table);
    }
}
