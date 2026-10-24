# HLD: OneNote conveniences

## What it does

| Feature | How |
|---|---|
| Paste screenshots | Ctrl+V with an image on the clipboard saves `Title.assets/yyyyMMdd-HHmmss.png` next to the page and inserts `![…](Title.assets/….png)`. Copied files (Explorer Ctrl+C) paste as attachments. |
| Drop files | Dropping files on a Markdown page copies them into `Title.assets/` and inserts links (images inline). Shift+drop, or dropping on other documents, opens the files instead. |
| Lists | Enter continues `-`/`*`/`+` bullets, numbered items (incremented), tasks (`- [ ]`) and `>` quotes; Enter on an empty item ends the list. |
| `[[` completion | Typing `[[` lists page titles; choosing one inserts `Title]]`. |
| To-do | Ctrl+1 cycles plain → `- [ ]` → `- [x]` → plain (see editing tools). The **To-do pane** lists open tasks of all notebooks grouped by due date (optionally completed ones); ticking one edits its page; clicking opens the page at that line. Dates, priorities, recurrence and reminders: see `tasks.md`. |
| Tags | `#tag` (letters first, `/` for hierarchy) or front matter `tags:`. Rendered as chips in the preview (click = search). The **Tags pane** lists all tags with page counts. |
| Templates | `Notebook → New page from template`: Markdown files in the notebook's `_templates/` folder, with `{{title}}`, `{{date}}`, `{{time}}`, `{{datetime}}`. "Edit templates…" creates the folder with a Meeting example. Pages are never created inside `_templates`. |
| Favorites / recent | `Go` menu: favorites (also from the page context menu) and the 15 most recent pages; Ctrl+E lists recent pages first. Stored in `session.json` (`favoritePages`, `recentPages`). |
| Links follow moves | After a rename or move (tree drag-and-drop), the moved pages' own relative links are rebased automatically; links in other pages that point at the old location — relative links and `[[wiki]]` titles — are updated after a confirmation listing the pages. |
| Wiki links to missing pages | Clicking one offers to create the page (see preview HLD). |

## Main code

| File | Responsibility |
|---|---|
| `Core/Notebooks/PageExtras.cs` | `PageAssets` (folder, unique names, Markdown link) and `PageTemplates` (list, placeholder filling). |
| `Core/Markdown/ListContinuation.cs` | What Enter does at the end of a list/quote line. |
| `Core/Markdown/TagParser.cs` | Markdig inline parser: `#tag` → `<a class="tag" href="tag:…">`. |
| `Core/Links/LinkUpdater.cs` | `Rewrite(text, pagePath, moves, oldTitle, newTitle)`: maps link targets through the moves (applied in order), rebases relative links of pages that moved, renames wiki titles. |
| `App/Views/EditorHost.Conveniences.cs` | Paste (including enabling Paste for images — AvalonEdit only allows text), drop, Enter handling, `[[` completion window. |
| `App/ViewModels/MainViewModel.Conveniences.cs` | Link updates after moves, templates, favorites, Tags/To-do data (`RefreshSummaries` after index updates), ticking tasks in files. |
| `App/Views/TagsPane`, `TodoPane`, `NotebookTree.Favorites.cs`, `MainWindow` (Go and template menus) | UI. |

## Contracts

- **Moves**: for a page, three `PathMove`s are applied — `Title.md`, `Title.assets/`, `Title/` (subpages); for a section or file, one. `LinkUpdater.Unmap` recovers a page's previous path so its relative links can be rebased.
- **Task edits from the To-do pane** go through the open tab when there is one (undoable, autosaved), otherwise directly to the file with its original encoding and line endings, followed by an index refresh.
- The To-do pane's checkbox is bound two-way to `TaskEntry.Done`; the setter performs the edit, so mouse, keyboard and UI Automation (screen readers) behave the same.

## Extending safely

- New placeholders: add them to `PageTemplates.Apply` and the HLD table.
- Anything that moves files must call `UpdateLinksAfterMove(before, newPath)` after `OnItemMoved`.
- Tags and tasks come from the search index (`PageFacts`); change extraction there so search, panes and preview stay consistent.
