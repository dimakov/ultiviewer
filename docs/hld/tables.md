# HLD: Spreadsheet-like tables

## What it does

Markdown pipe tables edit like a small spreadsheet (in the spirit of Obsidian's Advanced Tables), in Edit,
Split and Live modes (in Live, the table shows as source while the caret is in it):

| Action | How |
|---|---|
| Next / previous cell | Tab / Shift+Tab; the cell's text is selected so typing replaces it; Tab after the last cell adds a row |
| Next row | Enter goes to the same column one row down, adding a row at the end; Enter on an empty last row removes it and continues below the table (after a blank line, so new text doesn't join the table) |
| Rows and columns | Format → Table editing, or the editor's context menu → Table: insert above/below/left/right, delete, move up/down/left/right |
| Alignment | Align column left / center / right / default (`:--`, `:-:`, `--:`, `---`) |
| Sort | Sort rows by the caret's column, A → Z or Z → A: numbers by value, text naturally ("Item 2" before "Item 10"), empty cells last |
| Tidy | Every action rewrites the table with padded, aligned columns (East Asian wide characters count double) |
| Paste from Excel / Sheets | Tab-separated cells (every row the same number of cells, 2+ columns) become a table; numeric columns are right-aligned; cells with line breaks get `<br>` and `|` is escaped. Inside a table they become rows below the caret's row. Ctrl+Alt+V pastes the plain text instead. Not inside fenced code. |

## Main code

| File | Responsibility |
|---|---|
| `Core/Markdown/TableEditor.cs` | `Find` (the table around the caret, outside fences, and the caret's cell), `Apply(TableAction)` → one `TextEdit` (the whole table rewritten, caret or cell selection), `Format`, `ParseTsv` / `FromTsv` (Excel quoting), `InsertRows`, `Width`. |
| `Core/Markdown/MarkdownTable.cs` | Row splitting with escaped pipes and code spans, delimiter row and alignments. |
| `App/Views/EditorHost.Tables.cs` | Key handling, the Table commands, paste conversion, the context submenu. |
| `App/Editor/EditorCommands.cs` | `Table…` commands (Markdown-only) and `PasteAsText` (Ctrl+Alt+V). |

## Design notes

- **Whole-table rewrites.** Each action parses the table into rows and alignments, changes the model and writes
  the table again, so every edit leaves aligned columns and is one undo step. When the result equals the current
  text (Tab through an already tidy table) only the selection moves and no undo step is recorded.
- **Cell under the caret** = unescaped pipes before the caret (outside code spans), minus a leading pipe. The
  delimiter row counts as the header.
- **Paste detection** is deliberately strict (rectangular, 2+ columns, 2+ rows unless the text ends with a line
  break, as a copied Excel row does) so ordinary text with tabs stays text.
- The table menu commands don't compute "caret is in a table" for CanExecute: WPF re-queries CanExecute on every
  input event and that would parse the document each time. Outside a table they report it in the status bar.

## Extending safely

- New table actions: add to `TableAction`, handle in `TableEditor.Apply` (mutate `rows` / `alignments`, set the
  target `row` / `column`), add a command in `EditorCommands` (+ `MarkdownOnly`), a line in
  `EditorHost.TableCommands`, a menu item, and a test in `TableEditorTests`.
- Keep `Format` the only writer, so cell positions for the caret stay right.
