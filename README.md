# Ultiviewer

A native Windows note-taking app: OneNote's notebook → section → page hierarchy on top of plain Markdown files, Notepad++-style tabs and editing, and a live Markdown preview.

See [PLAN.md](PLAN.md) for goals and milestones and [docs/hld/](docs/hld/) for the design of each feature.

## Build

Requires the .NET 10 SDK. Visual Studio is not needed.

```bat
build.cmd            :: build everything and run the tests
build.cmd build      :: build only
build.cmd run        :: build and start the app
build.cmd publish    :: self-contained release build -> artifacts\Ultiviewer-win-x64.zip
```

`build.cmd` prefers a per-user SDK in `%LOCALAPPDATA%\Microsoft\dotnet` (installed with `dotnet-install.ps1 -Channel 10.0`) and otherwise uses the `dotnet` on `PATH`.

From WSL, keep the repo on the Windows drive and call the Windows toolchain:

```sh
cmd.exe /c "cd /d C:\Users\intel-nuc\Documents\codex\ultiviewer && build.cmd"
```

The app is built self-contained (`win-x64`), so `Ultiviewer.exe` runs without a machine-wide .NET 10 runtime.

## Layout

| Path | Contents |
|---|---|
| `src/Ultiviewer.Core` | UI-free logic: notebooks, files, Markdown, search, settings. Unit-tested. |
| `src/Ultiviewer.App` | WPF application: windows, view models, editor and preview hosts. |
| `tests/Ultiviewer.Core.Tests` | xUnit tests for Core. |
| `docs/hld` | High-level design documents, one per feature. |
| `research` | Background research (OneNote search internals). |
