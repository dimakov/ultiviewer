# HLD: Two editors side by side

## What it does

The editor area can show **two editors**, side by side (View → Two editors side by side, **Ctrl+\\**) or stacked
(View → Two editors stacked). Each has its own tab strip, tabs, active tab, scrolling, find bar, view-mode
buttons and (in Split/Preview modes) preview. The same document can be open in both: it is one text with one
undo history, shown twice, each view with its own caret and scroll position and its own view mode (for example
Live on the left and the Markdown source on the right).

- Splitting opens the current document in the second editor and focuses it. Clicking anywhere in an editor (or
  F6 / **Ctrl+Shift+\\**) makes it the focused one, marked by an accent line on top; commands, the status bar and
  "open" actions (tree, search results, links, quick open) act on the focused editor.
- Tab context menu: *Open in the other editor too* (a second view) and *Move to the other editor*; tabs can also be
  dragged onto the other strip. Opening a document that is already open in the other editor activates it there.
- Closing a tab that the other editor also shows just removes that view; closing the last view closes the
  document (saving or asking as usual). When one side has no tabs left, the window returns to a single editor;
  *Single editor* merges the second editor's tabs into the first.
- The layout, ratio (drag the splitter), both tab lists, per-view modes and the focused side are restored on
  the next start.

## Main code

| File | Responsibility |
|---|---|
| `App/ViewModels/EditorGroup.cs` | A group: tabs, active tab, focus flag, per-group view modes (`ModeOf`, `SetMode`, `Adopt`, `Forget`). |
| `App/ViewModels/MainViewModel.Groups.cs` | `Primary` / `Secondary`, `IsSplit`, `SplitVertical`, `FocusedGroup`, `OpenTabs`, `GroupOf`, split / unsplit / move / open-in-other commands, keeping `ActiveTab` = focused group's active tab. |
| `App/ViewModels/MainViewModel.Documents.cs` | Group-aware open, add, close (`CloseTab(tab, group)`), cycle, pin, drag moves, session save/restore of both groups. |
| `App/Views/EditorPane.xaml(.cs)` | Tab strip + mode buttons + `EditorHost` bound to one group; claims focus on click. |
| `App/Views/EditorHost.xaml(.cs)` | Follows its group's active tab; per-editor view states; answers focus / go-to-line / select requests only when its group is focused. |
| `App/Views/TabStrip.xaml(.cs)` | Bound to a group; close/move/drop within or between groups. |
| `App/MainWindow.xaml(.cs)` | `ArrangePanes` (one pane, or two with a splitter in columns or rows), `ActiveEditorHost`, F6 cycling. |
| `Core/Settings/SessionState.cs` | `IsSplit`, `SplitVertical`, `SplitRatio`, `SplitTabs`, `SplitActiveTab`, `FocusedGroup`. |

## Design notes

- **`ActiveTab` keeps its meaning** ("the document being worked on") so the many features built on it (save,
  formatting, history, view mode, word count…) work unchanged; it follows `FocusedGroup.ActiveTab`, and setting it
  to a tab of the other group moves the focus there. Code that means "every open document" uses `OpenTabs`.
- **One `DocumentTabViewModel` per document**, shared by both groups; view state that differs per view lives in
  the `EditorHost` (caret, selection, scroll) and in the second group's mode map. The tab's own properties still
  receive the last captured state for the session.
- The session stores each group's tabs; a document in both lists is restored once and shown twice.
- `JsonFileStore` writes NaN/Infinity as named literals so an unset window position can't stop the session save.

## Extending safely

- New "act on the document" features: use `ActiveTab`; new "for every open document" loops: use `OpenTabs`.
- New per-view state: store it in `EditorHost` (keyed by tab), not in `DocumentTabViewModel`.
- New tab operations: take the group from `GroupOf(tab)` (prefers the focused group) and use `AddToGroup` /
  `RemoveFromGroup`, which keep active tabs valid and collapse an empty side.
- More than two groups would generalize `Primary`/`Secondary` into a list; `OtherGroup` and the session's two lists
  are the places that assume two.
