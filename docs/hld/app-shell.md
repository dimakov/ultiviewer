# HLD: Settings, shortcuts, single instance and release

## Settings

`File → Settings…` (Ctrl+,) edits a copy of `AppSettings` (`SettingsViewModel`) and applies it on Save: theme, editor font and size, word wrap, line numbers, whitespace, current-line highlight, autosave (notebook pages / other files / delay), defaults for new files (line endings, UTF-8 BOM), the default Markdown view, date/time formats. `MainViewModel.ReloadSettingsIntoView` re-applies theme and editor options to the running window.

Stored in `%APPDATA%\Ultiviewer\settings.json` (camelCase JSON; unknown or broken files fall back to defaults).

## Keyboard shortcuts

`Services/KeyboardShortcuts.cs` owns every shortcut:

- **Application commands** (new page, save, tabs, zoom, view mode, go to page, settings…) are bound as window `KeyBinding`s in code (`BindApplicationCommands`).
- **Editor commands** are `EditorCommands` routed commands; their gestures are replaced from overrides at startup (`ApplyEditorOverrides`) before the editor exists.
- Overrides: `settings.json → "shortcuts": { "<Name>": "Ctrl+Alt+D" }`, several gestures separated by `|`, `""` removes the shortcut. Names are listed in Settings → Keyboard shortcuts. Invalid gestures are logged and ignored.
- AvalonEdit's own bindings that clash with our **default** gestures stay removed even after a remap, so e.g. Ctrl+D never silently deletes a line.
- Menu `InputGestureText` shows the defaults.

## Single instance

`Services/SingleInstance.cs`: a `Local\Ultiviewer-<hash>` mutex per user and state folder. A second launch (e.g. double-clicking a `.md` in Explorer) sends its file arguments as JSON over the named pipe of the same name (`PipeOptions.CurrentUserOnly`), allows the first instance to take the foreground, and exits. The running instance restores and activates its window and opens the files. If the pipe can't be reached the new process simply runs. The hash includes `AppPaths.LocalDirectory`, so `ULTIVIEWER_HOME` test runs never hand files to a real instance.

## Windows integration

`Services/FileAssociation.cs` (Settings → Windows integration) writes per-user keys only:

```
HKCU\Software\Classes\Ultiviewer.Document            DefaultIcon, shell\open\command = "<exe>" "%1"
HKCU\Software\Classes\Applications\Ultiviewer.exe    FriendlyAppName, shell\open\command, SupportedTypes (.md .markdown .txt)
HKCU\Software\Classes\<ext>\OpenWithProgids          Ultiviewer.Document
```

then broadcasts `SHCNE_ASSOCCHANGED`. Windows 10/11 don't allow silently taking over a default; the user confirms "Always use this app" once in Explorer's Open with dialog. "Remove" deletes exactly these keys.

## Accessibility and DPI

- Per-monitor-v2 DPI and long paths via `app.manifest`.
- F6 / Shift+F6 cycle keyboard focus between the left pane, the editor and the search results.
- Controls carry `AutomationProperties.Name`; list items (search results, tasks, page switcher) expose readable names via `ToString()`; the To-do checkbox is bound two-way so UI Automation toggles work like clicks.

## Release

`build.cmd publish` → `dotnet publish -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true` into `artifacts\publish\Ultiviewer\` and zips it to `artifacts\Ultiviewer-win-x64.zip` (≈80 MB zipped, ≈185 MB unpacked; no .NET install needed; WebView2 runtime is part of Windows 11 / current Windows 10). Unzip anywhere and run `Ultiviewer.exe`; use Settings → Windows integration to add it to "Open with". An installer (Inno Setup / MSIX) is not part of the repository yet.

## Extending safely

- New application shortcut: add a row to `KeyboardShortcuts.AppCommands` (and a menu `InputGestureText`).
- New setting: add it to `AppSettings`, `SettingsViewModel` (constructor + `ApplyTo`), the settings window, and wherever it is read.
