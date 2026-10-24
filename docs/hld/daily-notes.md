# HLD: Daily notes and quick capture

## What it does

- **Today's note** (File → Today's note, Ctrl+Shift+D, the tray menu) opens `<notebook>\Daily\2026-10-24.md`,
  creating it from the notebook's `_templates/Daily.md` (placeholders `{{date}}`, `{{time}}`, `{{title}}` and
  Obsidian-style `{{date:dddd d MMMM}}`) or with a dated heading.
- **Quick note**: a small always-on-top window for jotting a note without navigating: **Win+Alt+N** from any
  app (configurable; Settings → Daily notes and quick notes), File → Quick note…, or the tray icon. Ctrl+Enter
  adds the note to *Today's daily note* or the *Inbox* (`<notebook>\Inbox.md`) and closes; Ctrl+Shift+Enter /
  *Add and open* also opens the page; *Paste clipboard* appends the clipboard text. The last target is remembered.
- Notes are appended with the time: one line becomes `- 14:05 Call Dana` (consecutive ones form a list), longer
  notes get a bold time line. An open tab receives the text as an undoable edit and saves; a closed page is
  written directly, with both versions in the page history.
- **Notification area** (optional, off by default): with *Keep running in the notification area*, closing the
  window hides it so the shortcut keeps working; the tray icon opens the window, a quick note or today's note,
  and exits. File → Exit always quits.

Settings: `dailyNotesNotebook` (empty = first notebook), `dailyNotesFolder` (`Daily`), `dailyNoteFormat`
(`yyyy-MM-dd`), `quickCaptureHotkey` (`Win+Alt+N`, empty = off), `quickCaptureTarget`, `keepRunningInTray`.

## Main code

| File | Responsibility |
|---|---|
| `Core/Capture/QuickCapture.cs` | `DailyNotes` (paths, new-day text from the template, Inbox path) and `QuickCapture.Append`. |
| `Core/Notebooks/PageExtras.cs` | `PageTemplates.Apply`, now with `{{date:FORMAT}}`. |
| `App/ViewModels/MainViewModel.Capture.cs` | Capture notebook, today's note, `Capture` (to a tab or the file), creating pages, events for the window and settings. |
| `App/Views/QuickCaptureWindow.xaml(.cs)` | The quick-note window. |
| `App/Services/GlobalHotkey.cs` | `RegisterHotKey` on the main window's handle; gesture parsing ("Win+Alt+N"). |
| `App/Services/TrayIcon.cs` | WinForms `NotifyIcon` and its menu (the only WinForms use; its global usings are removed). |
| `App/App.xaml.cs` | Registers the shortcut and the tray icon after startup and again when settings change. |
| `App/MainWindow.xaml.cs` | Hide-instead-of-close in tray mode, `ShowFromTray`, opening the quick-note window. |

## Design notes

- The shortcut and tray are created in `App.OnStartup`, not in `MainWindow`, so anything else that hosts the
  window (the snapshot tool) never registers a system-wide shortcut.
- `RegisterHotKey` fails when another program owns the combination (OneNote uses Win+Alt+N for its own quick
  notes when it runs); the status bar says so and Settings takes another one. Gestures need at least one
  modifier.
- `ExitRequested` distinguishes "close the window" from "quit": File → Exit, the tray's Exit and Windows session
  end set it.
- The quick-note window activates itself only when shown normally (`ShowActivated`), which keeps off-screen
  test rendering from taking focus.

## Extending safely

- More targets (a chosen page, a section's newest page): add to `CaptureTarget`, `CapturePath`, the window's
  combo box and the settings.
- Previous/next day navigation can reuse `DailyNotes.PathFor` with `date ± 1`.
