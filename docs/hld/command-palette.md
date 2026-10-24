# HLD: Command palette and shortcut editor

## What it does

- **Command palette** (Ctrl+Shift+P, Help → Command palette): a box at the top of the window that finds any
  command by name and runs it. Each entry shows its menu path (`Edit › Line operations › Duplicate line`), its
  current shortcut, and a check mark for options that are on (`View › Word wrap`). Typing filters fuzzily
  (`line dup` finds Duplicate line through its menu path); with an empty box the commands run most recently come
  first. Only commands that can run right now are listed, so formatting commands appear only in a Markdown page.
- **Shortcut editor** (Settings → Keyboard shortcuts, also Help → Keyboard shortcuts): click a command's shortcut
  box and press the new keys; Backspace or Delete removes it; the arrow button restores the default; "Restore all
  defaults" resets everything. A filter box finds commands by name, settings name or keys. Two commands with the
  same keys are marked in red ("Also used by Go to page"). Keys shared on purpose show a gray note instead (see
  below). Saving applies the shortcuts immediately: menus, window bindings and the editor all switch without a
  restart.

## Main code

| File | Responsibility |
|---|---|
| `Core/Search/CommandRanking.cs` | Palette ordering: recent first for an empty query, otherwise fuzzy title score, half score for a menu-path match, bonus for recent use; `Remember` keeps the recent list (20). |
| `Core/Settings/SessionState.cs` | `RecentCommands`: palette keys (menu paths) run lately, newest first. |
| `App/Views/CommandPaletteWindow.xaml(.cs)` | The palette window and `PaletteItem`; runs the chosen command after closing, at input priority, so focus is back in the main window. |
| `App/MainWindow.Palette.cs` | Builds the palette entries from the menus; `ApplyShortcuts` and `RefreshMenuGestures` after Settings. |
| `App/Services/KeyboardShortcuts.cs` | The shortcut registry: `ShortcutRow`, `All`, `FindConflicts`, `GestureFor` (key press → gesture text), `Commands` and `GestureText` for the palette and menus, re-bindable window bindings. |
| `App/ViewModels/SettingsViewModel.cs` | Rows, filter view, `SetShortcut` / `ResetShortcut` / `ResetAllShortcuts`; `ApplyTo` stores only shortcuts that differ from the defaults. |
| `App/Views/SettingsWindow.xaml(.cs)` | The shortcut list and key capture; `ShowShortcuts` scrolls to it. |

## How it works

### Palette entries

`MainWindow.PaletteCommands` walks the main menu recursively when the palette opens. The Go menu and the
template menu are rebuilt first, because they are normally filled when they open. Each leaf `MenuItem` becomes an
entry:

- **Item with a command:** the entry executes the command with the item's `CommandParameter`. A routed command
  (editor commands, `ApplicationCommands.Undo`…) is executed and checked with the focused editor's text area as
  its target, because keyboard focus is in the palette while it is open.
- **Checkable item without a command:** the entry toggles `IsChecked`. These items are two-way bound to a setting,
  for example Word wrap.
- **Other items:** the entry raises `MenuItem.ClickEvent`, which runs the item's `Click` handler (Find in
  notebooks, Exit, Go-menu pages…).

Titles come from the header with the access-key underscores removed. A `RoutedUICommand` item without a header
shows the command's `Text`. Application and editor commands that have no menu item are added after the menu
entries, without a path. The palette leaves out its own command. Entries are built while the main window still has
focus, so `CanExecute` reflects the current document.

### Shortcuts

- **Application commands** (`AppCommands`) are window `KeyBinding`s. `BindApplicationCommands` remembers the
  bindings it added to each window, so it can remove and recreate them after a change.
- **Editor commands** are `EditorCommands` routed commands. `EditorHost` dispatches them itself in
  `PreviewKeyDown` by reading `command.InputGestures`. `ApplyEditorOverrides` therefore resets every command to its
  default (or overridden) gestures, and the change takes effect on the next key press.
- **Menus.** `RefreshMenuGestures` sets each menu item's `InputGestureText` from the registry, so the menus
  always show the current keys. The values written in XAML are only initial text. An item with a
  `CommandParameter` (Help → Keyboard shortcuts) keeps its own text.
- **Ctrl+Shift+F** stays hard-coded in `MainWindow.OnWindowPreviewKeyDown` and is listed as a fixed row.

### Conflicts

`FindConflicts` groups rows by canonical gesture text (`KeyGestureConverter`), so `Ctrl+OemPlus` and a typed
`ctrl+oemplus` compare equal.

- **Conflict (red).** Two rows that share keys and apply in the same places.
- **Note (gray).** The editor resolves some shared keys by document type, so these rows get a note instead of a
  conflict. If one row is Markdown-only formatting (`EditorCommands.MarkdownOnly`) and the other isn't, the
  formatting command wins in Markdown pages. Examples: Ctrl+U underlines in notes and lowercases elsewhere; Ctrl+-
  strikes through in notes and zooms out elsewhere.

### Storage

`settings.json → "shortcuts"` keeps the same contract as before: `{ "<Name>": "Ctrl+Alt+D" }`.

- Several gestures are separated by `|`.
- `""` removes a shortcut.
- The editor stores only the rows that differ from their defaults. Names it doesn't know (typed by hand) are kept.
- Capturing a key replaces all of a command's gestures with the one pressed. Restoring the default brings back
  alternatives such as Ctrl+W | Ctrl+F4.

Key capture handles these cases:

- It reads `SystemKey` for Alt chords and `ImeProcessedKey` under an IME.
- Plain Tab, Esc and Enter keep their usual dialog jobs.
- Letters and digits without Ctrl or Alt are rejected, because `KeyGesture` doesn't support them.

## Extending safely

- **New command.** Add it to a menu and it is in the palette automatically. If it also needs a shortcut, add a row
  to `KeyboardShortcuts.AppCommands` or give the `EditorCommands` entry a default gesture. The shortcut editor,
  conflict check and menu text follow from the registry.
- **Command without a menu item.** Commands that aren't in `AppCommands` or `EditorCommands` don't appear in the
  palette unless a menu item exposes them.
- **Menu items whose `Click` handler needs a sender.** A handler that expects a specific sender, such as a status
  bar button, must not be put in the main menu as is, because the palette raises `Click` with the item as sender.
- **Testing.** The snapshot tool's `palette` step (query, print, shot, run) and `shortcuts` step (change, print
  conflicts, render the list, apply) test both features off-screen.
