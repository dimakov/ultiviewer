# HLD: Task dates, recurrence and reminders

## What it does

Tasks are Markdown task items (`- [ ] …`). Metadata uses Obsidian Tasks' syntax, so pages stay portable:

| Write | Means |
|---|---|
| `📅 2026-10-25` / `[due:: 2026-10-25]` | due date |
| `⏳ …` / `[scheduled:: …]`, `🛫 …` / `[start:: …]` | scheduled / start date |
| `✅ …` / `[completion:: …]` | done date (added automatically) |
| `🔁 every week` / `[repeat:: every week]` | recurrence: every day / N days / week / N weeks / weekday / Monday, Friday / month (on the 15th, on the last) / year, optional `when done` |
| `🔺 ⏫ 🔼 🔽 ⏬` / `[priority:: high]` | priority (highest … lowest) |
| `⏰ 2026-10-25 09:00` | reminder (as Obsidian's Reminder plugin writes it) |

- **Completing** a task (Ctrl+1, its checkbox in Live or Preview, the To-do pane, a reminder's Done) adds
  `✅ today`; reopening removes it. A **recurring** task also gets its next occurrence, an open copy inserted
  above it with due, scheduled, start and reminder moved to the next date (from the old date, or from today for
  `when done`; an undated recurring task gets a due date).
- **To-do pane**: open tasks of all notebooks grouped **Overdue · Today · Upcoming (7 days) · Later · No date**
  (and Done when shown), sorted by priority and date; each shows its description without metadata, a relative
  due label ("Tomorrow", "2 days overdue"), priority, 🔁 and the reminder time.
- **Reminders**: when a `⏰` time arrives (checked every 30 s, for reminders less than a day old) a card appears
  at the bottom-right of the screen without taking focus: *Open*, *Done*, *Snooze 10 min* (rewrites the `⏰`
  time) or *Dismiss*. Shown reminders are remembered in the session. At startup the status bar summarizes
  today's and overdue tasks.
- In Live mode task metadata is drawn faint and slightly smaller.

## Main code

| File | Responsibility |
|---|---|
| `Core/Markdown/TaskInfo.cs` | `TaskInfo.Parse` (metadata + description), `TaskRecurrence.Next`, `TaskCompletion` (`Toggle`, `Cycle`, `Shift`, `SetReminder`). |
| `App/ViewModels/MainViewModel.Conveniences.cs` | `TaskEntry` (group, labels, glyphs), sorting in `RefreshSummaries`, `UpdateTaskLine` (tab or file), `ToggleTaskInPage`, `SnoozeTask`. |
| `App/ViewModels/MainViewModel.Links.cs`, `App/Views/EditorHost.Commands.cs` | Checkbox clicks and Ctrl+1 through `TaskCompletion`. |
| `App/Services/ReminderService.cs`, `App/Views/ReminderWindow.xaml(.cs)` | Reminder checks and cards (started by `App` only). |
| `App/Views/TodoPane.xaml(.cs)` | Grouped list (grouping on the default collection view). |

## Design notes

- Metadata is parsed from the task text when the To-do list is read: the search index keeps storing the raw
  task text, so no index schema change was needed and edits made by other apps are picked up.
- Reminders come from the To-do list, which covers notebook pages (indexed); tasks in loose files outside
  notebooks don't remind.
- Reminder cards are ordinary topmost windows (`ShowActivated = false`), so they work without the tray icon or
  Windows notification registration, and offer actions.

## Extending safely

- New metadata: extend `TaskInfo.Field()` (the single regex that both parsing and `Shift` use) and `Kind`.
- New recurrence phrases: `TaskRecurrence.Next`, with tests in `TaskInfoTests`.
