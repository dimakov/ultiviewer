# HLD: Outline, folding and structural editing

## What it does

- **Outline pane** (left panel → Outline): the active Markdown page's headings as a tree, updated while typing
  (350 ms after edits). The heading whose section holds the caret is highlighted; clicking a heading (or Enter)
  moves the caret there. Collapsed branches stay collapsed while the page is edited.
- **Folding** in every view mode of Markdown pages: heading sections (up to the next heading of the same or a
  higher level, trailing blank lines left visible), fenced code blocks, front matter and list items with
  children. Use the fold margin, **Ctrl+Shift+[** / **Ctrl+Shift+]** (fold / unfold the innermost region at the
  caret), or View → Fold all headings / Unfold all. A page's single top-level heading (its title) stays open
  when folding all. Folded regions are remembered per tab and editor and restored with the session.
- **Structural editing**: **Ctrl+Shift+↑/↓** on a heading moves its whole section past the previous / next
  section of the same level (not out of its parent); on a list item it moves the item with its children past its
  sibling. Elsewhere the lines move as before. **Tab / Shift+Tab** on a list item indents or outdents it with its
  children.

## Main code

| File | Responsibility |
|---|---|
| `Core/Markdown/MarkdownOutline.cs` | Line scan (front matter and fence aware): `Headings` with section ranges, `Folds`, `ListSubtree`, `MoveBlock`, `IndentListItem`. |
| `App/Views/EditorHost.Folding.cs` | AvalonEdit `FoldingManager` per shown document (reinstalled on tab switch), debounced fold updates, fold commands, fold state per tab. |
| `App/Views/EditorHost.Commands.cs` / `EditorHost.Formatting.cs` | Move lines → `MoveBlock` in Markdown; list Tab → `IndentListItem`. |
| `App/ViewModels/MainViewModel.Outline.cs`, `App/Views/OutlinePane.xaml(.cs)` | The outline tree and navigation. |
| `Core/Settings/SessionState.cs` | `OpenTabState.Folded`: first lines (1-based) of folded regions. |

## Design notes

- A quick line scan instead of a Markdig parse keeps outline and folds cheap on every edit; it recognizes ATX
  headings, setext headings (`===` always, `---` only after a blank-separated paragraph line, so a rule under a
  list is not a heading), fences and front matter.
- Moves swap two blocks and keep the blank lines where they were (`b, gap after a, a, gap after b`), so section
  spacing stays tidy; the caret stays at the same place inside the moved block.
- Folding and Live mode's table widgets both collapse lines; the Live transformer styles only the first line of
  a multi-line visual line, so a folded heading keeps its heading style.
- In Live mode the text starts at the left, right after the line numbers and the fold margin.

## Extending safely

- New fold kinds: add to `FoldKind` and emit them in `Folds`; they must nest properly (AvalonEdit requires
  sorted, non-crossing ranges).
- Keep `MarkdownOutline` line-based and allocation-light; it runs after every pause in typing.
