# Ultiviewer

A native Windows note-taking app: OneNote's notebook → section → page hierarchy on top of plain Markdown files, Notepad++-style tabs and editing, and a live Markdown preview.

Notes are ordinary `.md` files in ordinary folders, so Explorer, git, OneDrive and other editors keep working with them.

## Features

- **Notebooks**: any folder is a notebook; subfolders are sections; `Title.md` is a page; `Title/` next to it holds subpages; `Title.assets/` holds pasted images and attachments. Drag and drop to reorder, nest and move; section colors; Recycle Bin deletes; changes made outside the app show up immediately.
- **Editor**: tabs (preview tabs, pinning, reopen closed), syntax highlighting for Markdown and many languages, encoding and line endings preserved byte for byte, light/dark themes.
- **Saving**: notebook pages save themselves (like OneNote); other files use Ctrl+S (like Notepad++); unsaved buffers survive crashes and restarts; files changed by other programs reload or ask.
- **Version history** (Ctrl+Shift+H): versions kept as you edit and whenever another program changes a file; compare any version with the current text, copy from it or restore it; conflicts keep both sides.
- **Live view** (like Obsidian's Live Preview): edit notes formatted, with headings, bold, links, checkboxes, quotes, code blocks, tables and images drawn in place; Markdown syntax appears only where the caret is.
- **Preview**: Live / Edit / Split / Preview per tab (Ctrl+Shift+V) with scroll sync, tables, task lists, math, Mermaid diagrams, highlighted code, `[[wiki links]]` and `#tags`; offline and locked down (no scripts from notes).
- **Search** (Ctrl+Shift+F): indexed, live, accent- and case-insensitive, substring matching (works for Hebrew prefixes and code identifiers), `"phrases"`, `OR`, `-exclude`, `#tag`, `is:todo`; replace across the listed pages. **Go to page** (Ctrl+E).
- **Basic Text formatting** (like OneNote's Home tab): toolbar, Format menu and shortcuts for headings, bold/italic/underline/strikethrough, sub/superscript, highlight, font color, lists, quotes, links, tables and code, all written as Markdown.
- **Spreadsheet-like tables**: Tab / Shift+Tab / Enter move between cells and keep columns aligned; insert, delete, move, align and sort rows and columns; paste cells from Excel as a table.
- **Daily notes and quick notes**: Ctrl+Shift+D opens today's note (from a `Daily` template); Win+Alt+N from any app opens a small window that adds a time-stamped note to today's note or the Inbox; optional tray icon.
- **Two editors** (Ctrl+\\): side by side or stacked, each with its own tabs and scrolling; the same note can be open in both, for example Live on one side and source on the other.
- **Outline and folding**: an Outline pane of headings; fold sections, code, lists and front matter (remembered per page); Ctrl+Shift+↑/↓ moves a whole section or list item with its children; Tab indents list items with their children.
- **Task dates and reminders** (Obsidian Tasks syntax): due/scheduled dates, priorities, `🔁 every week` recurrence and `⏰` reminders; the To-do pane groups Overdue / Today / Upcoming; completing a recurring task creates the next one; reminder cards with Open / Done / Snooze.
- **Export and print**: a page or a whole section as one self-contained HTML file or a PDF (A4/Letter, margins), or printed; math, diagrams, images and page breaks (`<!-- pagebreak -->`) included.
- **Notepad++ tools**: find/replace bar with regex, go to line, duplicate/move/delete/join/sort lines, case conversion, bookmarks, column selection, smart highlighting.
- **OneNote conveniences**: paste screenshots, drop files as attachments, Ctrl+1 to-dos and a To-do pane across notebooks, Tags pane, templates, favorites and recent pages, list continuation, `[[` completion, links that follow renames and moves.

## Build

Requires the .NET 10 SDK. Visual Studio is not needed.

```bat
build.cmd            :: build everything and run the tests
build.cmd build      :: build only
build.cmd run        :: build and start the app
build.cmd publish    :: self-contained release -> artifacts\Ultiviewer-win-x64.zip
```

`build.cmd` prefers a per-user SDK in `%LOCALAPPDATA%\Microsoft\dotnet` (installed with `dotnet-install.ps1 -Channel 10.0`) and otherwise uses the `dotnet` on `PATH`.

From WSL, keep the repo on the Windows drive and call the Windows toolchain:

```sh
cmd.exe /c "cd /d C:\Users\intel-nuc\Documents\codex\ultiviewer && build.cmd"
```

The app is built self-contained (`win-x64`), so `Ultiviewer.exe` runs without a machine-wide .NET 10 runtime.

## Where things are stored

| What | Where |
|---|---|
| Notes | Your notebook folders |
| Order and colors | `.ultiviewer.json` (hidden) inside notebook folders |
| Settings | `%APPDATA%\Ultiviewer\settings.json` |
| Session, search index, unsaved-buffer backups, WebView2 data, error log | `%LOCALAPPDATA%\Ultiviewer\` |
| Version history | `%LOCALAPPDATA%\Ultiviewer\history\` (one folder per file) |

Set `ULTIVIEWER_HOME=<folder>` to keep all of the above (except notes) in one folder — handy for testing.

## Layout

| Path | Contents |
|---|---|
| `src/Ultiviewer.Core` | UI-free logic: notebooks, files, Markdown, links, search, settings. Unit-tested. |
| `src/Ultiviewer.App` | WPF application: windows, view models, editor and preview hosts. |
| `src/Ultiviewer.App/PreviewAssets` | Preview shell page and bundled JS/CSS libraries (see `vendor/NOTICE.md`). |
| `tests/Ultiviewer.Core.Tests` | xUnit tests for Core. |
| `tools/Ultiviewer.Snapshot` | Developer tool: renders the main window off-screen to PNG from a JSON script (see `docs/hld/live-preview.md`). |
| `docs/hld` | High-level design documents, one per feature. |
| `research` | Background research (OneNote search internals). |
