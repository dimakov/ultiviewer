# HLD: Notepad++-style editing tools

## What it does

| Feature | Keys | Notes |
|---|---|---|
| Find bar | Ctrl+F, F3 / Shift+F3, Enter / Shift+Enter | Incremental; highlights every match; "n of m"; match case, whole word, regex, in selection. |
| Replace | Ctrl+H; Enter = replace one, Ctrl+Enter = replace all | Regex replacements expand `$1`, `${name}`; replace all is one undo step. |
| Replace in files | Search panel → ⇄ | Replaces in the pages listed in the search results after a confirmation listing counts per page; each page opens in a tab and changes as one undoable edit. |
| Go to line | Ctrl+G | |
| Duplicate / delete / move lines | Ctrl+D, Ctrl+Shift+L, Ctrl+Shift+↑/↓ | Work on all lines touched by the selection. |
| Join lines | Ctrl+J | |
| Sort ↑/↓, remove duplicates, remove empty lines, trim trailing whitespace | Edit → Line operations | Selected lines, or the whole document when nothing is selected. |
| UPPER / lower / Title case | Ctrl+Shift+U, Ctrl+U, menu | Selection, or the word at the caret. In Markdown documents Ctrl+U underlines instead (see `formatting.md`); lowercase stays in the menu. |
| Bookmarks | Ctrl+F2 toggle, F2 / Shift+F2 next/previous, click the margin | Anchored to text, saved in the session. |
| Smart highlighting | select a word | Other occurrences of the selected whole word get a subtle background. |
| To-do | Ctrl+1 | Cycles plain → `- [ ]` → `- [x]` → plain list item on every selected line (OneNote's To Do tag). |
| Date / time | Alt+Shift+D / T / F | Formats from settings (`dateFormat`, `timeFormat`). |
| Column selection | Alt+drag, Alt+Shift+arrows | AvalonEdit built-in; typing edits all lines. |

## Main code

| File | Responsibility |
|---|---|
| `Core/Text/TextSearcher.cs` | Regex construction (escaping, whole word via lookarounds, case, 2 s timeout), `FindAll` (skips empty matches, respects context for `^`/lookbehind), replacement expansion, `ReplaceAll`. |
| `Core/Text/LineOperations.cs` | Pure line transforms (natural sort, dedupe, trim, join, case). |
| `Core/Markdown/TaskMarkers.cs` | `Cycle` for Ctrl+1. |
| `App/Editor/EditorCommands.cs` | `RoutedUICommand`s and their gestures (menus show the gestures). |
| `App/Views/EditorHost.Commands.cs` | Command bindings and implementations; shortcut dispatch in `PreviewKeyDown`; removal of conflicting AvalonEdit gestures. |
| `App/Views/FindBar.xaml(.cs)` | The find/replace bar. |
| `App/Editor/SearchHighlighter.cs` | Background renderer for matches and smart highlights (draws only the visible range). |
| `App/Editor/BookmarkMargin.cs` | Margin rendering and click-to-toggle; bookmarks live as `TextAnchor`s in `DocumentTabViewModel`. |
| `App/ViewModels/MainViewModel.Search.cs` | `ReplaceInResults`. |

## Key decisions

- **Shortcut dispatch happens in `EditorHost.PreviewKeyDown`.** AvalonEdit's text area marks arrow keys handled before ancestor input bindings run, and binds Ctrl+D to its own "delete line" through `AvalonEditCommands.DeleteLine.InputGestures`. We remove conflicting gestures from AvalonEdit's input handlers and commands, and run ours on the tunnelling event. In the find bar only find/replace/go-to shortcuts apply, so Ctrl+U etc. still edit the text box normally. Markdown-only formatting commands are tried first and only run in Markdown documents, and Ctrl+Alt gestures are skipped while AltGr is down (details in `formatting.md`).
- **Every multi-step edit is one undo step** (`TextDocument.RunUpdate()`), so Ctrl+Z reverts a whole "replace all", "move line" or Ctrl+1 on many lines.
- **Replace in files never writes files directly**: changes go through open tabs (undoable), and the normal save path (autosave for notebook pages, Ctrl+S otherwise) persists them.

## Extending safely

- New editor command: add it to `EditorCommands` (with a gesture if any), bind it in `InitializeCommands`, add a menu item in `MainWindow.xaml`. If the gesture is also used by AvalonEdit, `RemoveConflictingAvalonBindings` takes care of it.
- Pure text logic belongs in `Core/Text` with tests; the view only maps selections to line ranges and applies the result.
