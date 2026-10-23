# HLD: Notebooks and the notebook tree

## What it does

Shows one or more **notebooks** (ordinary folders) as a OneNote-style tree of sections, pages and subpages, and lets the user create, rename, move, reorder, color and delete them. Everything is stored as plain files, so changes made in Explorer, git or OneDrive show up in the tree, and the app's changes are visible to every other tool.

## Storage contract

| Tree item | On disk |
|---|---|
| Notebook | Any folder listed in `settings.json` → `notebooks[].path` (display name in `displayName`). |
| Section | A subfolder. Can be nested. |
| Page | `Title.md`. The title is the file name. |
| Page with subpages | `Title.md` plus a sibling folder `Title/` holding the subpages. Shown as one node. |
| Attachments | `Title.assets/` next to the page. Never shown in the tree. |
| Other file | Any other file, shown with a file icon. |
| Hidden | Names starting with `.`, files with the Hidden/System attribute, `*.uvtmp` temp files. |

**Order and color sidecar** — `.ultiviewer.json` in a folder (created with the Hidden attribute):

```json
{ "version": 1, "order": ["Inbox.md", "Cooking", "Projects"], "color": "#E07B39" }
```

- `order` lists entry names (`Title.md` for pages, folder name for sections, full name for files), compared case-insensitively.
- Merge rule (`OrderMerger.Apply`): listed entries that exist come first in that order; everything else follows — sections first, then pages/files, each in natural order ("Day 2" before "Day 10").
- A folder without `order` is sorted by that default. The app only writes `order` when the user reorders by drag-and-drop, or inserts into a folder that already has one. A sidecar with nothing in it is deleted.
- Unknown or corrupt sidecars are treated as empty and never overwritten unless the user changes order/color.

**Names** (`FileNameRules`): Windows-valid, ≤ 200 characters, no leading period (would be hidden), no `.assets` suffix, no reserved device names.

## Main code

| File | Responsibility |
|---|---|
| `Core/Notebooks/NotebookScanner.cs` | Folder → immutable `NotebookNode` tree, applying the mapping above. `ListEntries` gives one level's display order. |
| `Core/Notebooks/NotebookNode.cs` | Snapshot of an item; `ChildFolder` (where children live) and `AssetsFolder` encode the page conventions. |
| `Core/Notebooks/FolderMetadata.cs` | Sidecar model and `FolderMetadataStore` read/write/`UpdateOrder`. |
| `Core/Notebooks/OrderMerger.cs`, `NaturalStringComparer.cs` | Ordering rules. |
| `Core/Notebooks/NotebookOperations.cs` | Create page/section, rename, move (with optional index), delete, set order/color. A page always moves as `Title.md` + `Title/` + `Title.assets/`; renaming rewrites the page's own `Title.assets/…` links. |
| `Core/Files/AtomicFile.cs` | Temp-file-then-`File.Replace` writes. |
| `Core/Files/FileSystemMoves.cs` | Case-only renames, cross-volume folder moves, path containment checks. |
| `Core/Links/MarkdownLinks.cs`, `CodeRanges.cs` | Find/rewrite link destinations (inline, reference, HTML `src/href`, `[[wiki]]`), skipping code. |
| `App/ViewModels/TreeNodeViewModel.cs` | Tree item; `Apply` merges a new snapshot by path so expansion/selection survive rescans. |
| `App/ViewModels/MainViewModel.Notebooks.cs` | Commands (new/rename/delete/move/color/import), watchers, refresh, selection. |
| `App/Views/NotebookTree.xaml(.cs)` | TreeView, inline rename, context menus, drag-and-drop with a drop indicator. |
| `App/Services/NotebookWatcher.cs` | `FileSystemWatcher` per notebook with debounced `StructureChanged` / per-file `FileChanged`. |
| `App/Services/RecycleBinDeleter.cs` | Deletes via `SHFileOperation` with undo (Recycle Bin). |

## Data flow

```
user action ──► MainViewModel command ──► NotebookOperations (disk) ──► RefreshNotebook (sync rescan) ──► TreeNodeViewModel.Apply
Explorer/git change ──► FileSystemWatcher ──► debounce 300 ms ──► background ScanNotebook ──► dispatcher ──► Apply (if still the latest refresh)
```

Each refresh bumps a per-notebook version so an older background scan never overwrites a newer synchronous one. Watcher buffer overflows trigger a full rescan.

Drag-and-drop: the top quarter of an item means "before", the bottom quarter "after", the middle "inside" (pages: become a subpage). Files only accept before/after; notebooks only reorder among notebooks. "Before/after" materializes the folder's current order into the sidecar. Files dragged in from Explorer are **copied** into the target.

Hooks for other features (`partial` methods in `MainViewModel.Notebooks.cs`): `OnItemMoved(old, new)`, `OnItemDeleted(path)`, `OnNewPageReady(path)`, `OnFileChangedOnDisk(path)`, `OnNotebookRescanRequested(root)`.

## Persistence

- `%APPDATA%\Ultiviewer\settings.json` — notebook list and display names (`AppSettings`).
- `%LOCALAPPDATA%\Ultiviewer\session.json` — expanded paths, selected path, window placement, tree width (`SessionState`).
- `ULTIVIEWER_HOME=<folder>` redirects both (used for UI tests).

## Extending safely

- New tree item kinds: extend `NodeKind`, the scanner's classification, `TreeNodeViewModel.Glyph` and the context menu in `NotebookTree.xaml.cs`.
- Any operation that moves or deletes files must go through `NotebookOperations` so the page triple and the sidecars stay consistent, and must call `OnItemMoved`/`OnItemDeleted` so open documents follow.
- Keep Core free of WPF: everything under `Core/Notebooks` is covered by `tests/…/Notebooks`.
