# HLD: Version history

## What it does

Every file edited in Ultiviewer gets a browsable history (**File → Page history…**, Ctrl+Shift+H, the tab's or
page's context menu → *History…*). The window lists versions newest first with when and why they were kept, and
shows either the **changes since** the selected version (a line diff against the page's current text: green
added since, red removed since) or the **whole version**. *Copy selected text* copies a selection (or the whole
version) to paste back; *Restore this version…* replaces the page text as one undoable edit. The window is
modeless and follows the page while it is edited. While the tab has unsaved changes, *File on disk* compares the
saved file with the buffer.

A version is kept:

| When | Reason shown |
|---|---|
| Before the first save of a tab in this session (the file as it was on disk) | Before this session's edits |
| On save (autosave or Ctrl+S), at most once per interval (default 5 min; the first save in an interval wins) | Saved |
| When another program changes an open file without unsaved changes: the old text, then the new | Before another program changed it / Changed by another program |
| When that happens with unsaved changes (a conflict): your text and the disk text, whatever you answer | Your unsaved text during a conflict / Changed by another program |
| Before a restore | Before restoring an older version |
| Save As over an existing file: that file's text first | Before this session's edits |

Identical consecutive versions are not stored. Old versions are thinned: everything from the last day, the
newest per hour for 30 days, the newest per day after that, at most 500 per file. Settings → Saving turns
history off or changes the interval (`keepHistory`, `historyIntervalMinutes`).

## Main code

| File | Responsibility |
|---|---|
| `Core/History/VersionHistory.cs` | The store: `Record` (dedupe, interval, prune), `List`, `Read`, `Move`, `Prune`. |
| `Core/History/LineDiff.cs` | Unified line diff (Myers after trimming common prefix/suffix). |
| `App/ViewModels/MainViewModel.History.cs` | When to record (`RecordHistoryBaseline`, `RecordVersion`), `RestoreVersion`, the history command. |
| `App/ViewModels/MainViewModel.Documents.cs` | Calls into history from `SaveTab`, `SaveTabAs`, `CheckDiskState` and `OnItemMoved`. |
| `App/ViewModels/HistoryViewModel.cs` | Version list, comparison text and per-line diff kinds, restore. |
| `App/Views/HistoryWindow.xaml(.cs)`, `App/Editor/DiffRenderer.cs` | The window and the diff coloring in a read-only editor. |

## Storage contract

`%LOCALAPPDATA%\Ultiviewer\history\<key>\` (or `<ULTIVIEWER_HOME>\Local\history`), where `<key>` is the first 20
hex digits of SHA-256 of the upper-cased full path:

- `index.json`: `{ "path": "C:\\…\\Page.md", "versions": [ { "id": "20261024T102512345", "timeUtc": "…Z",
  "length": 84, "hash": "<sha256 of UTF-8 text>", "reason": "saved" } ] }`
- `<id>.txt.gz`: the version's text, UTF-8, gzipped. Versions store text, not bytes: restoring goes through the
  tab, which keeps the file's encoding and line endings.

History lives outside notebooks, so it never syncs or shows up in git. Renames and moves made in the app re-key
it (`Move`, also for whole folders); files renamed outside the app start a new history.

## Extending safely

- New moments worth keeping: call `RecordVersion(tab, text, reason)`; add a `VersionReason` (and its text in
  `HistoryViewModel.Describe`). Only `Saved` is thinned by the interval.
- Recording is best effort and must never block saving: the store swallows IO errors.
- Keep the ids sortable (`yyyyMMddTHHmmssfff`, an `x` suffix for same-millisecond versions); ordering ties rely on it.
- The snapshot tool's `save`, `external`, `confirm` and `history` steps reproduce the scenarios above off-screen.
