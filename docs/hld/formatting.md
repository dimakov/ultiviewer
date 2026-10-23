# HLD: Basic Text formatting

## What it does

OneNote's Home → **Basic Text** group, expressed as Markdown. Markdown documents get a toolbar above the editor,
a **Format** menu and OneNote's shortcuts. Every action edits the Markdown source, so the file stays plain text
and the preview shows the result.

| Action | Keys | Markdown written |
|---|---|---|
| Paragraph style | Ctrl+Alt+0 (normal), Ctrl+Alt+1…6 | `# ` … `###### ` prefix on every selected line |
| Bold / Italic | Ctrl+B / Ctrl+I | `**text**` / `*text*` |
| Underline | Ctrl+U | `<u>text</u>` |
| Strikethrough | Ctrl+- | `~~text~~` |
| Subscript / Superscript | toolbar, menu | `~text~` / `^text^` |
| Highlight | Ctrl+Alt+H | `==text==` |
| Font color | toolbar palette, menu | `<span style="color:#…">text</span>`; "Automatic" removes it |
| Inline code | Ctrl+` | `` `text` `` |
| Clear formatting | Ctrl+Space | strips inline markers, tags and color spans from the selection (or the caret line) |
| Bulleted / numbered list, quote | Ctrl+. / Ctrl+/ / menu | `- `, `1. ` (renumbered from 1), `> ` on every selected line |
| Indent / outdent list item | Tab / Shift+Tab on a list line | one indentation unit, like OneNote |
| To-do | Ctrl+1 | existing `TaskMarkers.Cycle` (see `editing-tools.md`) |
| Link | Ctrl+K | `[text](url)`; a URL on the clipboard is used, otherwise `https://` is selected to type over |
| Table, code block, horizontal line, date | toolbar, Insert menu | block inserted on its own line below the caret line |

Inline styles toggle: running one on text that already has that style removes it. With no selection, they act
on the word at the caret; with no word there, they insert an empty pair of markers with the caret between them.
The toolbar's style box follows the caret line's heading level.

The toolbar shows only for Markdown tabs that are not in Preview-only mode. View → **Formatting toolbar**
and the Settings checkbox turn it off (`showFormatBar` in `settings.json`).

## Main code

| File | Responsibility |
|---|---|
| `Core/Markdown/MarkdownFormatting.cs` | Pure text transforms: `ToggleInline`, `WordAt`, `SetHeading`, `ToggleList`, `ClearFormatting`, `SetColor`, `MakeLink`, `Table`, `CodeBlock`. Each inline call returns a `TextEdit` (range, replacement, selection to restore). |
| `App/Editor/EditorCommands.cs` | The routed commands and default gestures; `MarkdownOnly` lists the commands that exist only in Markdown documents. |
| `App/Views/EditorHost.Formatting.cs` | Command bindings, applying `TextEdit`s to the AvalonEdit document as one undo step, line transforms over the selected lines, Tab/Shift+Tab list indentation, toolbar visibility. |
| `App/Views/EditorHost.Commands.cs` | Shortcut dispatch (`PreviewKeyDown`), Markdown-only `CanExecute`, AltGr guard. |
| `App/Views/FormatBar.xaml(.cs)` | The toolbar. Buttons bind to the same routed commands with the text area as `CommandTarget`, so they behave exactly like the shortcuts and menu; the style box and color palette raise events that `EditorHost` handles. |
| `App/MainWindow.xaml` | Format menu and the View → Formatting toolbar toggle. |
| `App/Editor/Markdown.xshd` | Editor highlighting for `==highlight==` (the other markers were already highlighted). |

## Data flow

1. A key press, toolbar click or menu click raises a routed command at the editor's text area.
2. `EditorHost` reads the document text and selection and calls the matching `MarkdownFormatting` function.
3. The returned replacement is applied inside `TextDocument.RunUpdate()`, so Ctrl+Z undoes the whole action, and the selection is restored around the same text.
4. The normal save path (autosave for notebook pages) persists the change; the preview re-renders it through
   Markdig (`UseAdvancedExtensions` provides `~~`, `~`, `^`, `==`; raw `<u>` and `<span style>` pass through, and
   the preview's CSP allows inline styles).

There is no new persistence or network contract; the only new setting is `showFormatBar`.

## Key decisions

- **Markdown, not rich text.** Notes remain readable `.md` files. Markdown has no underline or color, so those use
  the two HTML forms GitHub-style renderers also accept (`<u>`, `<span style="color:…">`).
- **Marker runs decide whether a style is present.** `*` and `**` share a character, so `HasStyle` counts the run
  of marker characters around the text: italic is an odd run (1 or 3), bold a run of 2 or more. This makes
  `***both***` toggle each style independently.
- **Words never come from inside HTML tags.** A caret inside `</u>` (Ctrl+→ stops there) counts as just after the
  tag, so the tag name is never wrapped in markers.
- **Shortcut conflicts.** Markdown-only commands are tried first and only execute in Markdown documents, so in
  notes Ctrl+U underlines (OneNote) and in other files it still lowercases (Notepad++). Ctrl+- is strikethrough
  in notes (zoom out everywhere else); in notes, zoom out stays on Ctrl+numpad −, Ctrl+mouse wheel and the View menu.
- **AltGr is not Ctrl+Alt.** Windows reports AltGr as Ctrl+Alt; while Right Alt is down, Ctrl+Alt shortcuts are
  skipped so AltGr characters (₪, €, …) still type on keyboards that need them.
- **All shortcuts are remappable** through `shortcuts` in `settings.json`, like the other editor commands.

## Extending

- Add a new inline style by extending `InlineStyle` and `Markers` in `MarkdownFormatting`, a command in
  `EditorCommands` (and `MarkdownOnly`), a `Bind` line in `EditorHost.Formatting.cs`, a toolbar button and a menu
  item. Add a `MarkdownFormattingTests` case and a `MarkdownRendererTests.RendersEveryToolbarStyle` case so the
  preview is known to render it.
- Keep transforms in Core and pure (`string` in, `TextEdit`/lines out); the App layer should only apply them.
- Before choosing a default shortcut, check `EditorCommands.WithGestures`, AvalonEdit's built-in gestures (the
  dispatcher removes clashes from AvalonEdit, which silently disables AvalonEdit's version) and AltGr layouts.
