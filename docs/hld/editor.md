# HLD: Editor, tabs and file handling

## What it does

Opens notebook pages and any other text file in Notepad++-style tabs, edits them with AvalonEdit (syntax highlighting, rectangular selection, line numbers, zoom), saves them without ever corrupting their encoding or line endings, and keeps unsaved work safe across crashes and restarts.

- **Notebook pages** (`.md` inside an open notebook) **autosave** ~1 s after the last edit, when their tab is left, and when the app closes — the OneNote model.
- **Other files** use explicit **Ctrl+S** with a dirty dot on the tab — the Notepad++ model. Both are configurable (`autosaveNotebookPages`, `autosaveExternalFiles`).
- **Unsaved buffers are never lost**: untitled tabs and unsaved external files are snapshotted to the backup folder every 5 s and on exit; closing the app does not prompt, the next start restores them (Notepad++ "remember unsaved changes").
- **Changes made by other programs** are picked up: a clean tab reloads silently (keeping the caret); a tab with unsaved edits asks first. Autosave never overwrites a file that changed on disk behind its back.

## Main code

| File | Responsibility |
|---|---|
| `Core/Files/TextFileFormat.cs` | Encoding detection (BOM, BOM-less UTF-16, strict UTF-8, ANSI code page fallback) and dominant line ending; `Encode` writes the exact same bytes back. |
| `Core/Files/TextDocumentFile.cs` | `Load` (shared read), `Save` (atomic), `Inspect` (binary / too large), `FileStamp` (mtime + size) and SHA-256 content hash. |
| `Core/Files/BackupStore.cs` | Backup snapshots in `%LOCALAPPDATA%\Ultiviewer\backup\*.txt`. |
| `Core/Text/TextStatistics.cs` | Word count for the status bar. |
| `App/ViewModels/DocumentTabViewModel.cs` | One open document: `TextDocument` (own undo stack; dirty = `!UndoStack.IsOriginalFile`), `Format`, `Language`, preview/pinned/deleted flags, known stamp + hash, view state. |
| `App/ViewModels/MainViewModel.Documents.cs` | Open/save/close/reopen, preview-tab replacement, autosave timers, disk checks, backups, session capture/restore, tab commands. |
| `App/ViewModels/MainViewModel.View.cs` | Word wrap, line numbers, whitespace, current line, zoom, theme. |
| `App/Views/EditorHost.xaml(.cs)` | The single `TextEditor`; swaps documents on tab change, restores caret/selection/scroll, applies palette and highlighting, Ctrl+wheel zoom. |
| `App/Views/TabStrip.xaml(.cs)` | Tabs: italic preview tabs, dirty dot, ✕ / middle-click close, drag to reorder, context menu. |
| `App/Editor/HighlightingRegistry.cs`, `Editor/Markdown.xshd` | AvalonEdit's bundled languages + our Markdown definition, each in a light and a generated dark variant. |
| `App/Services/ThemeService.cs` | Applies System/Light/Dark to WPF Fluent `ThemeMode`, tracks Windows theme changes, exposes the editor palette. |
| `App/MainWindow.xaml(.cs)` | Menus, shortcuts, status bar (Ln/Col, words, zoom, encoding / EOL / language pickers, save status). |

## Key flows

**Open** — `OpenDocument(path, preview)`: existing tab → activate; binary → status hint (preview) or default app; > 256 MB refused, > 32 MB confirmed; otherwise `TextDocumentFile.Load` → new tab. A single click in the tree opens a *preview tab* (italic) that the next single click replaces; editing, double-click, Enter or "Keep open" makes it permanent.

**Switch tabs** — `EditorHost` captures the old tab's caret/selection/scroll, assigns `Editor.Document = tab.Document`, applies the tab's highlighting, and restores its view state at `Loaded` priority (after layout).

**Save** — `SaveTab`: untitled → Save As; autosave first compares the file's stamp/hash with what we last saw and asks instead of overwriting (`CheckDiskState`); then `TextDocumentFile.Save` (temp file + `File.Replace`) and `MarkSaved` (new stamp + hash, undo stack marked original, backup deleted).

**Disk changes** — notebook files: `NotebookWatcher.FileChanged`; other files: window `Activated`. `CheckDiskState`: missing → "deleted on disk" (struck-through tab, saving recreates it); same hash → just update the stamp (our own save echoing back); clean → reload; dirty → confirm.

**Session** — on close (`CaptureDocumentState`): autosave what autosaves, back up the rest, write `session.json` → `tabs[]` (path or untitled name, caret, scroll, view mode, pinned, backup file, bookmarks) and `activeTab`; orphaned backups are deleted. On start (`RestoreTabs`): reload each file, apply its backup as an undoable edit (so it shows unsaved), restore view state.

## Encoding and line endings

Files keep the encoding and BOM they were read with; the status bar offers "Save with encoding" (UTF-8, UTF-8 BOM, UTF-16 LE/BE BOM, system ANSI) and "Reopen with encoding". New files use `defaultUtf8Bom` (off) and `defaultLineEnding` (CRLF). Typing Enter follows the line ending of the current line; "Line endings" in the status bar converts the whole document.

## Extending safely

- Never write a document file directly; go through `SaveTab` so stamps, hashes, backups and `OnDocumentSaved` (search indexing) stay consistent.
- Exceptions thrown while handling a `PropertyChanged` raised by a two-way binding are swallowed by WPF — wrap risky work (like loading highlighting) and log failures (`HighlightingRegistry.Get` does).
- AvalonEdit compiles xshd rule regexes with `ExplicitCapture`: don't use numbered backreferences in `Markdown.xshd`.
- New per-tab state: add it to `DocumentTabViewModel`, `OpenTabState`, `CaptureDocumentState` and `RestoreTabs`.
