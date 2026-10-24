# HLD: Live view (formatted editing, Obsidian style)

## What it does

**Live** is a fourth view mode next to Edit / Split / Preview (buttons right of the tabs, View menu,
Ctrl+Shift+V cycles Live → Edit → Split → Preview). The editor still edits the Markdown source, but shows it
formatted, like Obsidian's Live Preview:

- Syntax is hidden: `**bold**` shows as **bold**, `## Title` as a large heading, `[label](url)` as a link,
  `[[Page|alias]]` as "alias".
  - **By default, marks stay hidden even at the caret.** Once text is formatted, only the formatting shows (see
    "Hidden marks"). Block syntax still opens for editing: tables, pictures and rules show their source at the
    caret, and code fences on their own line.
  - **Optional Obsidian behavior.** With View → "Show Markdown marks at the caret" (`showMarksAtCaret`), moving
    the caret into an element (or selecting it) reveals its markers in a faint color. Heading, quote, list and
    rule markers then reveal per line; code blocks, tables and front matter reveal as a whole block.
- Widgets: bullets become dots, `- [ ]` becomes a clickable checkbox (checked tasks are struck through),
  `> ` becomes a quote bar, `---` a horizontal rule, `![alt|300](img.png)` the image (optional width as in
  Obsidian), pipe tables a rendered grid. Clicking a table cell or an image moves the caret there and shows
  the source.
- Styles: headings by level, bold/italic/strike/underline/highlight/sub/superscript, inline code and code
  blocks (monospace, panel), tags (pill), `<span style="color:…">` colors, math (italic), HTML comments (faint).
- A proportional font (`liveFontFamily`, default Segoe UI) in a centered column (`liveMaxWidth`, default 820
  px; 0 = full width); code, tables and front matter keep the editor font. No line numbers or current-line band.
- Clicking a rendered link opens it (URL, relative file, `[[wiki link]]`, `#tag` search); while a link shows as
  source, Ctrl+click opens it. Backspace at the start of a list item's text removes its bullet or checkbox.

New installs open Markdown in Live; choosing a mode on a tab also makes it the default (existing behavior).

## Main code

| File | Responsibility |
|---|---|
| `Core/Markdown/LiveMarkup.cs` | Turns Markdown into `LiveSpan`s (start, length, `LiveStyle`, reveal range, value, URL/text) indexed by line. Uses Markdig with precise source positions and a lean pipeline. `RevealRegion` gives the lines a caret/selection affects. |
| `Core/Markdown/MarkdownTable.cs` | Pipe-table parsing with source positions per cell (also the base for spreadsheet-like tables). |
| `App/Editor/Live/LivePreview.cs` | Controller: enable/disable on a `TextEditor`, lazy re-analysis after edits, reveal tracking and redraws, link clicks, checkbox events, list Backspace, collapsing the lines under rendered tables. |
| `App/Editor/Live/LiveElementGenerator.cs` | AvalonEdit element generator: replaces unrevealed replacement spans with elements. |
| `App/Editor/Live/LiveTransformer.cs` | Line transformer: fonts, sizes, weights, decorations, colors; faint revealed markers. |
| `App/Editor/Live/LiveBackgroundRenderer.cs` | Code block / front matter panels, quote bars, rules. |
| `App/Editor/Live/LiveElements.cs` | `HiddenElement` (zero-width or fixed-width run), `BulletElement`, `TaskBoxVisual`. |
| `App/Editor/Live/LiveWidgets.cs` | Image and table widgets; Markdown → WPF inlines for table cells (reuses `LiveMarkup`). |
| `App/Editor/Live/LiveStyles.cs` | Fonts and brushes per theme. |
| `App/Views/EditorHost.Live.cs` | Mode switching (font, wrap, line numbers, highlighting off), centered column via `TextView.Margin`, clicks in the side margins. |

## How it works

```
TextDocument.Changed ──► LivePreview: stale, release tables touched by the edit, schedule full redraw (150 ms)
render a line ──► generator/transformer ask LivePreview.Markup ──► LiveMarkup.Analyze(text, previous)
caret/selection moved ──► RevealRegion(old) ∪ RevealRegion(new) ──► TextView.Redraw(those lines)
```

- **Spans, not a second document.** The source is never rewritten. `LiveStyle.Marker … Rule` are
  *replacements* (hidden or drawn as widgets unless revealed); the rest are styles that always apply.
  Every span carries a reveal range; it shows as source when the caret/selection touches that range
  (`IsRevealedBy`, inclusive at both ends so a caret right after `**` reveals it).
- **Hidden text** is a `VisualLineElement` whose run is a zero-width `TextEmbeddedObject`.
- **Hidden marks** (`LiveMarkup.Editing.cs` rules, applied by `LivePreview.HiddenMarks.cs`). With marks hidden
  at the caret, their positions become invisible traps, so the editor keeps the caret and edits out of them:
  - **Caret.** After every move, an empty caret snaps out of a line's hidden prefix (`## `, `- [ ] `, `> `) to
    `ProseStart`, and out of the inside of a mark (`SnapCaret`). Home therefore lands at the start of the text.
  - **Arrow keys.** ← and → repeat the move when the caret only crossed hidden marks (no visible movement);
    at the end of a line the last position on it is kept. ← at the start of a heading's or list item's text
    goes to the previous line.
  - **Backspace and Delete.** They skip hidden marks and delete the visible character beyond them. Deleting the
    only character of a formatted run removes its marks too, so no stray `****` is left. Backspace at the start
    of a heading, quote or list item removes that prefix; after a hidden inline mark at the line start, it
    joins the lines.
  - **Enter.** At the end of formatted text it steps past the closing marks (the new line isn't formatted). At
    the start, it steps before the opening marks. Inside, it closes the marks before the break and reopens
    them after it (`**bo**` / `**ld**`). `EditorHost` calls `PrepareEnter` before list continuation.

  Which spans stay hidden: replacement spans except tables, pictures, rules and multi-line ("block") marks
  (`HidesAtCaret`). The decision is `IsRevealedWithHiddenMarks`.
- **Tables** span several lines, so their element covers multiple document lines. AvalonEdit requires the
  lines under such an element to be collapsed (as folding does); `LivePreview` collapses them when it builds the
  widget and uncollapses them when the caret enters the table or an edit touches it.
- **Incremental analysis.** `LiveMarkup.Chunks` splits the text at blank lines where no block can continue
  (outside fences, `$$` math, `<!-- -->` comments and front matter, and only before unindented lines), and
  chunk results are cached by chunk text. A keystroke re-parses one chunk: ~15 ms for a 150 KB note instead
  of ~200 ms. Reference-style links whose definition is in another chunk are the known gap (they stay plain).
  Notes over 2 MB are shown unformatted.
- **Multi-line effects** (typing a ``` fence changes every line below it) are fixed by a full redraw 150 ms
  after typing pauses; the edited line itself redraws immediately.

## Testing

- `LiveMarkupTests`: what Live shows for a given caret (a test renderer replaces spans with symbols), styles,
  CRLF offsets, reveal regions, chunked = whole-document analysis, chunk reuse, and a performance bound.
- `tools/Ultiviewer.Snapshot`: renders the real main window **off-screen and never activated** from a JSON
  script (open, mode, caret, select, type, key, click a widget, dump text) to PNG files, so UI changes can be
  checked without taking focus from whoever is using the machine. It uses a plain `Application` with
  `AppResources.xaml` because `App` would run the real startup.

## Extending safely

- New syntax: add a `LiveStyle` (replacements must stay before `Rule` in the enum, `IsReplacement` relies on
  it), emit it in `LiveMarkup` with a reveal range, draw it in the generator (replacement) or transformer
  (style), and add a `Render`-style test.
- Anything that can span blank lines must be taught to `Chunks`, or chunked analysis will split it; the
  `ChunkedAnalysisMatchesTheWholeDocument` test is the safety net.
- Widgets are recreated whenever their line is redrawn: keep them cheap and cache decoded resources (see the
  image cache in `LiveWidgets`).
- Multi-line widgets must collapse their lines like `LivePreview.TableElement` does.
