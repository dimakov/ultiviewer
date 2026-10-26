# Ultiviewer — Plan

Working name: **Ultiviewer**. A native Windows note-taking app that combines:

- **OneNote's** notebook → section → page → subpage hierarchy, manual ordering, and no-save-button feel.
- **Notepad++'s** plain files on disk, tabs, fast editing, syntax highlighting, and search power.
- A live **Markdown preview**.

## 1. Goals and non-goals

**Goals**

- Notes are plain files in plain folders. Any folder can be opened as a notebook. No database or proprietary format, so notes still work with Explorer, git, OneDrive and other editors.
- Hierarchy with manual ordering and drag-and-drop: notebook → sections (nestable) → pages → subpages.
- A Markdown editor with syntax highlighting and a live preview: edit-only, split, or preview-only.
- Opens any text file (code, logs, config) in tabs with highlighting, like Notepad++.
- Native Windows: fast start, low memory, system light/dark theme, works fully from the keyboard.

**Non-goals for v1**

- OneNote's free-form canvas (drag-anywhere containers, ink, audio). Pages are linear text.
- Writing OneNote files. Importing reads them directly (M7) instead of going through OneNote's COM API.
- Our own sync service. Notebooks sync by living in OneDrive, Dropbox or git.
- macOS and Linux.

## 2. What the provided `ONENOTE.EXE` is (and isn't)

Static inspection (the file was not run):

- PE32+ x64 GUI, *Microsoft OneNote* **16.0.20430.20182**, 3.2 MB.
- It is only the **launcher**. It imports `onmain.dll`, `MSO*.dll` and `AppVIsvSubsystems64.dll` (the Click-to-Run virtualization layer). OneNote's real logic lives in `onmain.dll` and the shared Office libraries.
- The same build is already installed at `C:\Program Files\Microsoft Office\root\Office16\ONENOTE.EXE`.

**Consequences**

- It can't run on its own and contains no code we can reuse. Decompiling or patching it would also break the Office license. **We build from scratch.**
- Useful as a **UX reference**: run the installed OneNote side by side while building.
- Useful as a **design reference**: the search internals of `onmain.dll` were studied using Microsoft's public symbols, without copying any code. Findings: [research/onenote-search.md](research/onenote-search.md).
- Useful as an **import path (M7)**: the installed OneNote exposes the `OneNote.Application` COM API (`GetHierarchy`, `GetPageContent`). That is the supported way to migrate existing notebooks to Markdown.
- Do not commit it. Add it to `.gitignore`.

## 3. Tech stack

| Layer | Choice | Why |
|---|---|---|
| Language / UI | **C# on .NET 10 (LTS), WPF** | Native Windows desktop with mature tree, drag-and-drop, keyboard and theming support. It hosts both the editor control and WebView2. Builds with the .NET SDK alone; Visual Studio is not required. |
| Editor | **AvalonEdit** | WPF-native code editor (it powers ILSpy and SharpDevelop). Has syntax highlighting, folding, rectangular (Alt+drag) selection, and large-document support, and follows the same WPF theming as the rest of the app. |
| Markdown | **Markdig** | Fast. Supports CommonMark plus GFM (tables, task lists, footnotes, front matter). Every block carries its source line number, which scroll sync needs. |
| Preview | **WebView2** (the Edge runtime, v155 already installed) | Full HTML/CSS fidelity. highlight.js, Mermaid and KaTeX are bundled locally, so the preview works offline. Can print to PDF. |
| MVVM | **CommunityToolkit.Mvvm** | Source-generated properties and commands, with no framework lock-in. |
| Persistence | Filesystem plus small JSON sidecars (`System.Text.Json`) | Notes stay portable. |
| Search | SQLite **FTS5** through `Microsoft.Data.Sqlite`: one disposable index per notebook, plus a grep step that covers recent changes and computes highlights | Follows OneNote's current design, which moved off Windows Search. See [research/onenote-search.md](research/onenote-search.md). |
| Tests | xUnit on `Ultiviewer.Core` | |

**Rejected alternatives**

- **WinUI 3**: has the modern look, but no native code-editor control. The editor would end up as Monaco inside WebView2, which is a web app in a window.
- **C++/Win32 + Scintilla** (Notepad++'s own stack): the most native and smallest option, but roughly 3–5× the effort for the tree, drag-and-drop, theming and WebView2 plumbing.
- **Electron / Tauri**: not native.

**Fallback:** if multi-caret editing or files over 100 MB become must-haves, replace AvalonEdit with **Scintilla.NET** (the Notepad++ engine) hosted in a `WindowsFormsHost`. Keep all editor-specific code under `Editor/` so the swap touches one folder.

## 4. Storage model

### Mapping

| OneNote concept | On disk |
|---|---|
| Notebook | Any folder registered in settings |
| Section group / section | Subfolder (can be nested) |
| Page | `Title.md` (the file name is the page title) |
| Page with subpages | `Title.md` plus a sibling folder `Title/`. The tree shows them as **one** node with children. |
| Images and attachments | `Title.assets/` next to the page (hidden in the tree) |
| Manual order, section color | `.ultiviewer.json` in each folder |
| Other files (`.txt`, `.cs`, `.log`, …) | Shown in the tree with a file icon. They open in the editor, with no preview. |

```
Personal/                      ← notebook
  .ultiviewer.json
  Inbox.md
  Projects/                    ← section
    .ultiviewer.json
    Ultiviewer.md              ← page …
    Ultiviewer/                ← … with subpages
      Storage design.md
    Ultiviewer.assets/
      20261023-101500.png
  Recipes/                     ← section
    Bread.md
```

### Sidecar `.ultiviewer.json`

```json
{ "version": 1, "order": ["Inbox.md", "Projects", "Recipes"], "color": "#4F6BED" }
```

Rules (files on disk are always the source of truth):

- Entries in `order` that no longer exist are dropped.
- Files that are not listed are added at the end, sorted alphabetically.
- A folder without a sidecar is sorted alphabetically.
- The app writes a sidecar only when the user reorders items or sets a color.
- These rules make the sidecars tolerant of other tools, git merges and OneDrive.

### Per-user state (never stored inside a notebook)

- `%APPDATA%\Ultiviewer\settings.json`: registered notebooks, theme, fonts, autosave, default encoding and line endings, shortcut overrides.
- `%LOCALAPPDATA%\Ultiviewer\session.json`: open tabs, caret and scroll positions, view mode, tree expansion, window placement.
- `%LOCALAPPDATA%\Ultiviewer\backup\`: snapshots of unsaved buffers.
- `%LOCALAPPDATA%\Ultiviewer\Index\{notebook-id}.db`: the search index. It is disposable, rebuilt when the schema changes or the file is corrupt, and never stored in the notebook folder.

### File operations

- **Atomic save:** write `name.tmp` in the same folder, then `File.Replace` (or `File.Move` for a new file). A crash never leaves a half-written note.
- **Keep encoding and line endings exactly:** detect the BOM, UTF-8, UTF-16 or a legacy code page (register `CodePagesEncodingProvider`) and CRLF or LF on load, and write them back unchanged. Defaults for new files are configurable (UTF-8 without BOM; CRLF or LF is an open decision, see §10).
- **Delete** sends to the Recycle Bin (`Microsoft.VisualBasic.FileIO.FileSystem` with `RecycleOption.SendToRecycleBin`).
- **Rename or move a page** also moves `Title.assets/` and `Title/` and rewrites the page's own relative asset links. Rewriting links in other pages that point to it comes in M5.
- **Names:** reject Windows-invalid characters (`\/:*?"<>|`) and reserved names (`CON`, `NUL`, …) with an inline error.
- **Changes made outside the app:** a `FileSystemWatcher` per notebook, debounced (~300 ms), triggers a rescan of the affected folder. If the watcher's buffer overflows, rescan the whole notebook. F5 forces a refresh.
- **Open file changed on disk:** if it has no unsaved edits, reload it silently and keep the caret. If it does, ask the user to keep their version or reload (the same as Notepad++).
- Enable `longPathAware` in `app.manifest`.

## 5. User interface

```
┌──────────────────────────────────────────────────────────────────────────┐
│ File  Edit  View  Search  Notebook  Help                [Edit|Split|View]│
├───────────────┬──────────────────────────────────────────────────────────┤
│ ▾ Personal    │ [Inbox ●] [Meeting 10-21] [build.log] [+]                │
│   • Inbox     ├────────────────────────────┬─────────────────────────────┤
│   ▾ Projects  │ # Meeting 10-21            │  Meeting 10-21              │
│     ▸ Ultivi… │ - [ ] ship M1              │  ☐ ship M1                  │
│   ▸ Recipes   │ ```cs                      │  ┌────────────┐             │
│ ▸ Work        │ var x = 1;                 │  │ var x = 1; │             │
│               │ ```                        │  └────────────┘             │
├───────────────┴────────────────────────────┴─────────────────────────────┤
│ (collapsible) Find results: 12 hits in 4 pages                           │
├──────────────────────────────────────────────────────────────────────────┤
│ Ln 3, Col 7 │ UTF-8 │ CRLF │ Markdown │ 412 words │ Saved                │
└──────────────────────────────────────────────────────────────────────────┘
```

- **Tree:** one pane for notebooks, sections and pages. This is simpler than OneNote's three panes; three panes could become an alternate layout later.
  - Drag-and-drop to reorder or nest. F2 renames, Del deletes.
  - Context menu: new page, new section, rename, delete, color, open in Explorer, copy path.
- **Tabs (Notepad++ style):**
  - A dirty marker shows unsaved changes. Middle-click closes a tab, and tabs can be reordered and pinned.
  - A single click in the tree opens a temporary preview tab (italic) that the next click replaces. It becomes a normal tab once edited, which stops tabs piling up.
- **Saving:**
  - **Notebook pages autosave** (OneNote feel): about 1 s after the last keystroke, on tab switch, and when the app loses focus. A file is written only if its content actually changed.
  - **Files from outside a notebook** use an explicit Ctrl+S and a dirty marker (Notepad++ feel).
  - Both behaviours are configurable.
- **Session restore:** reopens tabs, caret position, scroll position and view mode. Unsaved external buffers are snapshotted to `backup\` every few seconds and restored after a crash, like Notepad++'s "remember unsaved changes".
- **View modes:** Edit, Split or Preview, set per tab.
- **Theme:** follows the system light/dark setting. The editor colors and the preview CSS follow it too. Use WPF's Fluent `ThemeMode`; if it proves immature, fall back to our own light and dark ResourceDictionaries.

### Default shortcuts (overridable later)

| Keys | Action |
|---|---|
| Ctrl+N / Ctrl+Shift+N | New page / new section in current location |
| Ctrl+O, Ctrl+S, Ctrl+W, Ctrl+Tab | Open file, save, close tab, next tab |
| Ctrl+F / Ctrl+H | Find / replace in current document |
| Ctrl+Shift+F | Find in notebook (results panel) |
| Ctrl+E | Quick-open page by title (OneNote's search key) |
| Ctrl+G | Go to line |
| Ctrl+D, Ctrl+Shift+↑/↓ | Duplicate line, move line |
| Alt+drag | Column selection |
| Ctrl+Shift+V | Cycle Edit → Split → Preview |
| Ctrl+1 | Toggle `- [ ]` / `- [x]` on the current line (OneNote's To Do tag) |
| Alt+Shift+D | Insert date |

## 6. Markdown preview pipeline

1. A text change in the editor starts a debounce timer (~200 ms).
2. Markdig renders the HTML with `UseAdvancedExtensions()` and `UseYamlFrontMatter()`. A custom renderer adds `data-line="N"` to each block element.
3. The first load opens a shell page (`preview.html`). Later updates go through `PostWebMessageAsJson`, and the shell swaps in the new body. There is no full reload, so scrolling doesn't jump and nothing flickers.
4. **Local resources** use virtual hosts:
   - `https://note.local/` maps to the page's folder, so relative images work.
   - `https://app.local/` maps to the bundled CSS, highlight.js, Mermaid and KaTeX.
5. **Links** are handled in `NavigationStarting` / `NewWindowRequested`:
   - a relative `.md` link opens that page in a tab;
   - `http(s)` links open in the default browser;
   - `#anchor` links scroll the preview.
6. **Scroll sync:**
   - The editor's top visible line maps to the nearest `data-line` element (done in JS).
   - Scrolling the preview posts its line number back, and the editor scrolls to match.
   - Only one side drives at a time, which prevents feedback loops.
7. **Checkboxes:** clicking a task checkbox in the preview toggles `[ ]` / `[x]` in the source.
8. **Security** (opened files may come from untrusted sources):
   - The shell page sets a strict CSP: `script-src https://app.local` with no inline scripts and no inline event handlers.
   - Host objects are disabled; the page talks to the app only through web messages.
   - DevTools are off in Release builds.
   - Raw HTML in notes still renders, but scripts in notes never run.
9. **Export:** `CoreWebView2.PrintToPdfAsync` produces a PDF. HTML export is the rendered body plus the CSS.

## 7. Project layout

```
ultiviewer/
  PLAN.md
  README.md
  Ultiviewer.slnx
  Directory.Build.props          nullable on, warnings as errors
  Directory.Packages.props       central package versions (pin latest stable at M0)
  build.cmd                      build + test entry point (callable from WSL)
  .gitignore                     bin/ obj/ *.user ONENOTE.EXE
  src/
    Ultiviewer.Core/             net10.0, no WPF: all testable logic
      Notebooks/                 NotebookScanner, NotebookNode, OrderFile, NotebookOperations
      Files/                     TextDocumentFile (load/save), EncodingDetector, AtomicWriter
      Markdown/                  MarkdownRenderer, LineAttributeRenderer, WikiLinkExtension
      Search/                    QueryParser, QueryNode, SqlCompiler, SearchIndex, Indexer, GrepProvider, SearchSession
      Settings/                  AppSettings, SessionState
    Ultiviewer.App/              net10.0-windows WPF exe
      App.xaml, MainWindow.xaml, app.manifest
      ViewModels/                MainViewModel, TreeNodeViewModel, DocumentTabViewModel
      Views/                     NotebookTree, TabStrip, StatusBar, FindPanel, QuickOpen
      Editor/                    AvalonEdit setup, .xshd highlighting, line commands
      Preview/                   PreviewHost (WebView2 bridge)
      Platform/                  Recycle Bin, shell integration, single instance
      Assets/preview/            preview.html, preview.css, highlight.js, mermaid, katex
  tests/
    Ultiviewer.Core.Tests/
  docs/hld/                      one high-level design doc per feature
```

## 8. Milestones

Each milestone ends with a runnable app and small, focused commits.

### M0: Toolchain and skeleton (~½ day)

- Install the **.NET 10 SDK** on Windows: `winget install Microsoft.DotNet.SDK.10`. The machine currently has only the .NET 6 and 8 *runtimes*, with no SDK and no Visual Studio, and Visual Studio isn't needed.
- `git init` inside `ultiviewer/`. It currently sits untracked inside the parent `codex` repo.
- Create the solution, Core, App and Tests projects, plus `.gitignore` and `build.cmd`. Show an empty main window.
- To build from WSL, run `cmd.exe /c "cd /d C:\Users\intel-nuc\Documents\codex\ultiviewer && build.cmd"`. Keep the repo on `C:`, because the WPF build has to run under the Windows `dotnet`.
- **Done when:** the build and the test run are green and the window opens.

### M1: Notebook tree

- Register and open a folder as a notebook. The scanner builds the node tree:
  - a folder is a section;
  - an `.md` file is a page;
  - a page plus a same-named folder is a page with subpages;
  - `*.assets` folders are hidden.
- Read, merge and write the sidecar order files.
- Create, rename, delete (to the Recycle Bin) and drag-and-drop move. Context menu. Refresh from the `FileSystemWatcher`.
- Tests: scanner against temp folders, order merging, rename moving the assets and subpage folders, invalid names.
- **Done when:** a 3-level hierarchy built in the app looks the same in Explorer, and changes made in Explorer appear in the app.

### M2: Editor and tabs

- One AvalonEdit tab per document. Markdown highlighting, plus bundled definitions for common languages, chosen by extension.
- Load and save with encoding and line-ending detection, preserved exactly, using atomic writes.
- Autosave for notebook pages; dirty marker and Ctrl+S for external files.
- Open any file with Ctrl+O, by dragging it from Explorer, or from command-line arguments.
- Handle changes made on disk. Status bar showing Ln/Col, encoding, line endings, language, word count and save state. Session restore.
- **Done when:** killing the app mid-edit loses at most ~1 s of typing, and an unchanged UTF-16 LE CRLF file round-trips byte for byte.

### M3: Markdown preview

- Implement the pipeline from §6: the three view modes, light and dark CSS, code highlighting, Mermaid, clickable task checkboxes, link handling, relative images, scroll sync.
- **Done when:**
  - a note with tables, code, images, task lists and links renders correctly offline;
  - typing updates the preview without flicker;
  - clicking a link to another page opens it in a tab.

### M4: Notepad++ editing features

- Find/replace bar: regex, match case, whole word, search within the selection.
- **Notebook search**, modeled on OneNote; details in [research/onenote-search.md](research/onenote-search.md) §5.
  - **Index:** one SQLite database per notebook, filled by a background indexer.
    - Rows are **Markdown blocks** (heading, paragraph, list item, code block, table) with their start line.
    - Two FTS5 tables: `unicode61 remove_diacritics 2` with prefix indexes, and `trigram` for substring matches (Hebrew prefix letters, code identifiers).
    - `Tags` and `Tasks` tables.
    - A page is skipped when its mtime, size and hash are unchanged.
  - **Queries:** the query string becomes a tree, which is compiled to parameterized SQL.
    - Syntax: words (prefix match, all must appear), `"phrase"`, `OR`, `-word`, `#tag`, `is:todo` / `is:done`.
    - Scope: page, section, notebook or all notebooks, filtered by path.
  - **Session:**
    - The index query runs alongside a grep of open buffers and files changed since indexing.
    - Grep confirms each hit and computes highlights.
    - If the index is unavailable, the search falls back to a full grep.
    - Results stream to the UI in batches, and editing the query cancels and restarts the search.
  - **Results panel:** page, block snippet with highlights, and line; clicking a result opens the page at that line.
  - **Replace across files** after a confirmation step.
- Quick open (Ctrl+E): fuzzy search by title and path, from the same index.
- Go to line. Line operations: duplicate, delete, move, sort, trim trailing whitespace, join. Case conversion. Column editing.
- Bookmarks. Show whitespace and line endings. Word wrap. Zoom.
- Change encoding or line endings from a menu in the status bar. Override the detected language.
- **Done when:** a regex replace across a notebook can be previewed, applied, and undone per file.

### M5: OneNote conveniences

- Pasting an image from the clipboard saves `Title.assets/yyyyMMdd-HHmmss.png` and inserts `![](…)`. Dropping a file into the editor copies it into the assets folder and inserts a link.
- Insert date/time. Ctrl+1 toggles a to-do. Page templates (a new page can start from a file in `_templates/`).
- Recent pages and favorite pages.
- `[[Page title]]` wiki links (a Markdig extension) that resolve within the notebook, with autocomplete after typing `[[`.
- Renaming a page offers to update links to it in other pages, showing the changes first.
- Tags written as `#tag` or in front matter (`tags:`), with a tag pane.

### M6: Polish and release

- Settings window: font, theme, autosave interval, default encoding and line endings, shortcut overrides.
- Accessibility: UI Automation names and full keyboard navigation. Per-monitor high-DPI support.
- Single instance: a second launch passes its file arguments to the running instance over a named pipe.
- Publish with `dotnet publish -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true` and ship a zip. Then build an installer (Inno Setup or MSIX) with a `.md` file association and an "Open with Ultiviewer" entry in Explorer.
- **Done when:** on a fresh Windows account, after installing, double-clicking a `.md` in Explorer opens it in the running app.

### M7: OneNote import

- Reads OneNote's files directly, so OneNote doesn't need to be installed and never touches the source:
  a `[MS-ONESTORE]` revision-store reader plus the `[MS-ONE]` content model. The COM API (`GetHierarchy` /
  `GetPageContent`) was the original plan; it was dropped because it needs the installed desktop OneNote, works
  through the user's live notebooks and can't be tested without them.
- **Inputs:** `.onepkg` packages (a CAB archive, unpacked to a temp folder), notebook folders / `.onetoc2`, and
  single `.one` sections, from the menu or dropped on the window.
- A converter turns pages into Markdown: outlines become paragraphs and lists, tables become tables, pictures and
  attached files go to the assets folder, to-do tags become `- [ ]`, other tags `#tags`, OneNote page links
  `[[wiki links]]`. Ink, equations' layout and cloud-only files have lossy fallbacks (placeholder + report).
- Writes a new notebook folder (staged, then moved into place) and never modifies the source.
- **Import preview:** *File → Import from OneNote…* shows section groups, sections and pages with check boxes and
  page counts; selecting a page shows it converted (the normal preview renderer, and its Markdown), with what will
  be lossy listed underneath. The target folder and name are chosen there. **Import** writes only the checked
  items, with the same conversion that was previewed, and then shows the report.
- **Later:** sections saved by OneNote for the web / OneDrive (the FSSHTTPB packaged format) need a second store
  reader feeding the same model; they are listed and reported as unreadable until then.
- Details: [docs/hld/onenote-import.md](docs/hld/onenote-import.md).

### M8: Live preview editing (Obsidian style)

- A fourth view mode, **Live**, next to Edit / Split / Preview: the Markdown source is edited in place but shown formatted. Markers (`**`, `#`, `[`…`](url)`, fences) are hidden except where the caret or selection is, so the text never jumps between a source and a rendered copy.
- Built inside AvalonEdit: a Core analyzer turns Markdig's syntax tree (with precise source positions) into styled spans with "reveal ranges"; App-side an element generator hides or replaces markers (bullets, checkboxes, images, tables, rules), a line transformer applies fonts, sizes and colors, and a background renderer draws code blocks, quote bars and rules.
- Proportional font and a readable line width in Live mode; code, tables and front matter keep the monospace font. Clicking a link opens it, clicking a checkbox toggles it, clicking a table or image reveals its source.
- **Done when:** headings, emphasis, links, wiki links, tags, lists, tasks, quotes, code blocks, images, tables and rules render while typing, and each mode can be switched to per tab.

### M9–M20: Additions (priority order from user feedback)

| # | Milestone | Scope for the first version | Notes |
|---|---|---|---|
| M9 | Version history | Snapshot every save (and every external change) into a per-user history store; History window with a timeline, line diff against the current text or disk, restore all or the selected text; conflicts keep both versions. | Content-addressed store with pruning (keep all for 24 h, hourly for 30 days, daily after). |
| M10 | Spreadsheet-like tables | Tab / Shift+Tab between cells with auto-alignment, Enter for a new row, insert/delete/move rows and columns, column alignment, paste TSV/Excel into a table. | Shares `MarkdownTable` with the Live table widget. |
| M11 | Quick capture and daily notes | Global hotkey (`RegisterHotKey`) opens a small capture window; append to Inbox or today's daily note; clipboard prefill; "Open today's note" with a template. | Needs the app running; adds a tray icon so it can stay in the background. |
| M12 | Two documents side by side | Two editor groups (vertical or horizontal), each with its own tabs, scrolling and view mode; the same document can be open in both. | The biggest refactor: the single `EditorHost` becomes one per group. |
| M13 | Outline, folding, structure | Outline pane of headings; folding of headings, lists and code blocks; move a heading section or list subtree up/down; folded sections remembered per page. | AvalonEdit `FoldingManager`. |
| M14 | Task dates and reminders | `📅 2026-10-25`, `⏳`, `🔁 every week`, `⏫/🔼/🔽` (Obsidian Tasks syntax); Today / Overdue / Upcoming in the To-do pane; completing a recurring task creates the next one; Windows notifications for due times. | Same syntax as Obsidian Tasks, so notes stay portable. |
| M15 | HTML / PDF export and print | Export a page or section to standalone HTML (inlined CSS and images) or PDF (WebView2 `PrintToPdfAsync`); print with preview; `<!-- pagebreak -->`. | Reuses the preview renderer, so math, Mermaid and code highlighting come for free. |
| M16 | Search inside PDFs and images | Extract PDF text (PdfPig) and OCR images (`Windows.Media.Ocr`) in the background; index them as attachment blocks with page numbers; results show the attachment and page. | Requires a Windows 10 19041+ target framework for OCR. |
| M17 | Backlinks and heading links | Links pane: pages linking here, outgoing links, broken links, unlinked mentions; `[[Page#Heading]]` completion and navigation. | Uses the existing index and link resolver. |
| M18 | Command palette and shortcut editor | Ctrl+Shift+P palette over every command with its shortcut; a shortcut editor with conflict detection in Settings. | Builds on `KeyboardShortcuts`. |
| M19 | Saved searches and properties | Front-matter properties indexed (`status:`, `project:`…), queried as `status:done`; saved searches in the tree; results as a sortable table. | Extends the search grammar and index schema. |
| M20 | Formatted paste and web capture | Paste HTML (browser, Word) as Markdown with lists, links and tables; "Clip web page…" downloads an article with its images, source URL and date. | A browser extension is out of scope; the URL clipper covers the same workflow. |

### Later

OCR of pasted images with `Windows.Media.Ocr`, with the text indexed as a block; stemming expanded at query time; HTML/PDF export of whole sections; git integration (commit on idle); per-section encryption; a plugin API.

## 9. Risks and mitigations

| Risk | Mitigation |
|---|---|
| WPF can't draw on top of WebView2 ("airspace") | Keep inline overlays (find bar, autocomplete) in the editor area. Quick open is a `Popup` or a separate window, and those have their own HWND. |
| AvalonEdit limits (no multi-caret, very large files) | Scintilla.NET fallback (§3); editor code stays isolated in `Editor/`. |
| `FileSystemWatcher` drops events (OneDrive, network drives, buffer overflow) | Debounce, rescan the whole notebook on overflow, and a manual F5 refresh. |
| Sync conflicts (OneDrive creates `Title-DESKTOP.md`) | Shown as a separate page in v1; conflict detection later. |
| Autosave causing churn in git or OneDrive | Debounced, and a file is written only when its content changed. |
| Paths longer than 260 characters | `longPathAware` manifest. |
| Building from WSL | Use Windows `dotnet` through `cmd.exe`, with the repo on `C:`. |

## 10. Open decisions

- **App name:** *Ultiviewer* is a working title.
- **Layout:** a single tree (current plan) or OneNote's three panes.
- **Default line endings for new notes:** CRLF (Windows default) or LF (friendlier if notebooks live in git).
- **`[[wiki links]]`:** Obsidian and others understand them, but GitHub doesn't render them.

## 11. Next step

Start **M0**: install the .NET 10 SDK, run `git init`, and create the solution skeleton.
