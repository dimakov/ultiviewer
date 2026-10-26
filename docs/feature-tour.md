# Feature tour: how to see each new feature

**First, run the new build.** Close the running Ultiviewer, including its tray icon if "keep running in the
notification area" is on. A second launch hands its files to an instance that's already running, so if the old one
is still open you'd see the old version. Then run one of these from the repo folder:

```bat
build.cmd run        :: debug build, starts the app
build.cmd publish    :: rebuilds artifacts\publish\Ultiviewer (then start Ultiviewer.exe there)
```

Every feature below works on your own notebooks. The quickest test bed is a new page (Ctrl+N).

## Writing and formatting

| Feature | How to see it |
|---|---|
| **Live view** (Obsidian-style) | Open any note. Markdown opens in Live by default; the **Live / Edit / Split / Preview** buttons are at the top right, and Ctrl+Shift+V cycles through them. Type `## Heading`, `**bold**`, `- [ ] task`, a table or `![](picture.png)`: they render as you type, and once formatted only the formatting shows. View → "Show Markdown marks at the caret" brings back Obsidian's way, where the marks appear at the caret. If a note shows raw `**` and `#` everywhere, it is in **Edit** mode: click **Live**, and set Settings → "Markdown opens in" to Live for new tabs. Click a checkbox to tick it, or a link to follow it (Ctrl+click on the line you're editing). |
| **Basic Text formatting** (OneNote's Home tab) | The toolbar under the tabs: style box, B / I / U / strikethrough / sub / superscript, highlight, font colour, lists, to-do, quote, link, Table, code. The same actions are in the Format menu. Shortcuts: Ctrl+B, Ctrl+I, Ctrl+U, Ctrl+- (strikethrough), Ctrl+Alt+H (highlight), Ctrl+Alt+1…6 (headings), Ctrl+Alt+0 (normal text), Ctrl+K (link), Ctrl+1 (to-do). |
| **Spreadsheet-like tables** | Toolbar **Table**, or Format → Table. Inside a table: Tab / Shift+Tab move between cells and realign the columns, and Enter adds a row. Format → Table editing (or right-click) inserts, deletes, moves, aligns and sorts rows and columns. Copy cells in Excel and press Ctrl+V in a note to get a table. |
| **Outline and folding** | Open a note with several headings, then the **Outline** tab on the left; click a heading to jump to it. The ⊟ marks in the margin fold sections, code blocks and lists; folds are remembered per page. Ctrl+Shift+[ / ] fold or unfold a section; View → Fold all / Unfold all. Ctrl+Shift+↑/↓ moves a whole section or list item with its children. |
| **Task dates, recurrence, reminders** | Write `- [ ] Pay rent 📅 2026-11-01 🔁 every month ⏰ 2026-11-01 09:00`. The **To-do** tab groups tasks into Overdue / Today / Upcoming. Ticking a recurring task adds the next one. At the ⏰ time a reminder card appears with Open / Done / Snooze. |
| **Hebrew and other right-to-left text** | Write a note in Hebrew: once most lines are Hebrew it turns right to left (status bar shows **RTL**). English words and code lines inside keep their own order. Ctrl+Alt+R or Ctrl+Right Shift forces right to left, and Ctrl+Alt+L or Ctrl+Left Shift forces left to right; the status bar button or Format → Text direction → Automatic goes back. The Preview direction is set per paragraph. The default is in Settings → Text direction. |
| **Formatted paste** | Copy part of a web page, a Word document or a Google Doc and press Ctrl+V in a note. Headings, bold, links, lists and tables arrive as Markdown, and pasted pictures are saved next to the page. Ctrl+Alt+V pastes plain text. |

## Pages and notebooks

| Feature | How to see it |
|---|---|
| **Version history** | Edit a page for a while, then File → Page history… (Ctrl+Shift+H). Pick a version to see what changed, then copy from it or **Restore**. Versions are also kept when another program changes the file. |
| **Daily notes and quick notes** | Ctrl+Shift+D (File → Today's note) opens today's note. A notebook's `_templates/Daily.md`, if it has one, is the template. From any program, **Win+Alt+N** opens a small window that adds a time-stamped note to today's note or the Inbox; File → Quick note… opens the same window. The shortcut, the target and the tray icon are in Settings. |
| **Two documents side by side** | View → Two editors side by side (Ctrl+\\) or stacked. Right-click a tab → Move to the other editor. Ctrl+Shift+\\ switches between the two editors. The same note can be open in both, for example Live on one side and Edit on the other. |
| **Backlinks and heading links** | Open a page that other pages link to with `[[Its title]]`, then the **Links** tab. It shows which pages link here, this page's own links (broken ones flagged) and unlinked mentions, each with a **Link** button. Type `[[Page#` to pick one of that page's headings. |
| **Export to HTML / PDF, print** | File → Export page as HTML or PDF… (Ctrl+Shift+E) shows a preview with page size and margin settings, then saves an `.html` or `.pdf` file. Ctrl+P prints. To export a whole section, right-click it in the tree → Export as HTML or PDF…. `<!-- pagebreak -->` starts a new page. |
| **Export and import as zip** | Right-click a notebook or section → Export as zip… (or File → Export notebook as zip…). File → Import notebook from zip… brings it back with the same page order. |
| **Import from OneNote** | In OneNote: File → Export → Notebook → OneNote Package (`.onepkg`). In Ultiviewer: File → Import from OneNote… and pick that file (a `.one` section or a notebook folder's `.onetoc2` also works, and you can drop them on the window). The window lists section groups, sections and pages with check boxes; ⚠ marks pages that don't convert exactly. Click a page to see it rendered (**Preview**) and as **Markdown**, with what is lost underneath. Choose the name and folder, then **Import**: the new notebook opens in the tree, and the window shows the report. Sections saved by OneNote for the web (OneDrive) are listed as "can't be read" for now. |
| **Web clipper** | Copy a web page's address, then File → Clip web page… (the address is filled in). It creates a page next to the selected one containing the article, its pictures, and source and date in the front matter. |

## Finding things

| Feature | How to see it |
|---|---|
| **Search inside PDFs and pictures** | Drop a PDF or a screenshot into a page (it goes to `Page.assets/`). After a few seconds, Ctrl+Shift+F finds words inside it. Such results show a paper clip and "p. N". It can be turned off in Settings. |
| **Properties and saved searches** | Add front matter to a few pages, for example `status: active`, `priority: 2`, `due: 2026-11-01`, or a `rating:: 4` line. Then Ctrl+Shift+F and search `status:active`, `priority:>=2`, `due:<2026-12-01` or `rating:*`. Click **Table** to see the properties as sortable columns (click a header to sort). Click the ☆ button to save the search; it appears under **Saved searches** above the notebook tree. |
| **Command palette** | Ctrl+Shift+P (or Help → Command palette), type a few letters ("wrap", "dup line", "export") and press Enter. Recently used commands come first. |
| **Shortcut editor** | Help → Keyboard shortcuts (or Settings, Ctrl+,). Click a shortcut, press the new keys and save; it applies right away. Clashes are marked in red. |
