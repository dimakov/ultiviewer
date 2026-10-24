# HLD: Right-to-left text (Hebrew, Arabic)

## What it does

### Editor (Edit, Live, and the source side of Split)

Each document shows left to right or right to left:

- **Automatic (default).** A document reads right to left when at least as many of its prose lines are Hebrew,
  Arabic or another RTL script. Front matter, code blocks, inline code and link targets don't count, and a tie goes
  to the first line. A new page switches to RTL as soon as Hebrew is typed; it is re-detected 0.6 s after edits.
- **Chosen per file.** Ctrl+Alt+R or **Ctrl+Right Shift** sets right to left; Ctrl+Alt+L or **Ctrl+Left Shift** sets
  left to right (the Windows Notepad and Word convention). The choice is also available from Format → Text
  direction or the **RTL/LTR** button in the status bar, which also offers "Automatic". Choices are remembered per
  file in the session.
- **Default.** Settings → New pages and files → Text direction (Automatic / Left to right / Right to left) applies
  to every file without its own choice.

In an RTL document:

- the text is right-aligned, and line numbers and fold marks move to the right;
- Live bullets, checkboxes and quote bars sit on the right;
- the arrow keys move visually: → goes toward the line's end on screen.

Lines written the other way keep their own word order. English code or an English sentence in a Hebrew note reads
`print("hello, world")`, not `("print("hello, world`. A Hebrew line in an English note reads right to left. Live
tables take their direction from their cells, so a Hebrew table puts its first column on the right. Pictures and
checkmarks are never mirrored.

### Preview, export and print

Every paragraph, heading and table cell has `dir="auto"`, so it takes its direction from its own first letters.
Lists, list items, quotes and tables get an explicit `dir="rtl"` or `"ltr"` worked out from their source; the
reason is under "Design notes". CSS uses logical properties (`padding-inline-start`, `border-inline-start`,
`margin-inline`), so bullets, checkboxes and quote bars follow. Code blocks, math and inline code stay left to right.

## Main code

| File | Responsibility |
|---|---|
| `Core/Text/BidiText.cs` | `IsRightToLeft`, `FirstStrong`, `ProseStart` (after `#`, `>`, list markers and `[ ]`), `LineDirection`, `Detect` (document majority), `TextDirectionMode`. |
| `Core/Markdown/MarkdownRenderer.cs` | `dir` attributes on blocks. |
| `App/PreviewAssets/preview.css` | Logical properties; LTR code and math. |
| `Core/Settings/AppSettings.TextDirection`, `SessionState.TextDirections` | Default and per-file choices. |
| `App/ViewModels/DocumentTabViewModel.cs` | `DirectionMode`, `IsRightToLeft`, status bar label and tooltip. |
| `App/ViewModels/MainViewModel.Direction.cs` | Initial direction per tab, detection after edits (debounced), `SetDirection`, re-applying after Settings. |
| `App/Editor/BidiLineGenerator.cs` | Directional embedding for lines written the other way. |
| `App/Views/EditorHost.Bidi.cs` | `FlowDirection` of the editor, Ctrl+Shift side keys, swapped arrow keys on such lines, command bindings. |
| `App/Editor/EditorCommands.cs` | `RightToLeft` (Ctrl+Alt+R), `LeftToRight` (Ctrl+Alt+L), `AutomaticDirection`. |
| `App/Editor/Live/LiveWidgets.cs`, `LiveElements.cs` | Table direction from its text; images and checkboxes never mirrored. |

## Design notes

- **One direction per document.** AvalonEdit lays out every line with the `TextView`'s `FlowDirection`. Its
  paragraph properties are built once per layout pass, and WPF's `TextFormatter` can't be wrapped because it has
  internal abstract members. So alignment is per document.

- **Word order per line.** `BidiLineGenerator` adds zero-length elements to every line written against the
  document's direction:
  - at the start of its prose, a strong mark of the document direction (LRM/RLM), then a WPF `TextModifier` with
    `HasDirectionalEmbedding` in the line's direction;
  - at the end of the line, a `TextEndOfSegment`.

  This is how WPF gives a `Run` its own direction. WPF ignores the Unicode embedding characters (LRE/RLE): that
  was tried first. Strong marks alone were also tried; they fix a line's edges but not mixed words in its middle.
  The mark before the embedding keeps an English line's `- ` or `> ` on the right in a Hebrew note. In an English
  note WPF still places a Hebrew line's prefix at its right end, so that line reads right to left as a whole.

- **Caret behaviour of the inserted elements.** They cover no document text and return no caret stops, so a
  document offset maps to the column after them.

- **Arrow keys.** AvalonEdit swaps ← and → when the text area is RTL. On a line that reads the other way,
  `EditorHost` runs the opposite movement or selection command, so the caret still follows what is on screen.

- **Ctrl+Shift side keys.** The direction changes when the Shift key is released, and only if no other key was
  pressed in between, so Ctrl+Shift+V and other chords don't trigger it.

- **HTML `dir="auto"` on containers.** `dir="auto"` skips children that have their own `dir`, so a list of
  `dir="auto"` items would always be LTR. That is why containers get an explicit direction computed with
  `BidiText.Detect` over their source span.

## Extending safely

- **New Live widgets.** Set `FlowDirection = LeftToRight` on anything that must not be mirrored (pictures,
  drawings, icons). Text inherits the editor's direction.
- **New block kinds in the preview.** Text blocks get `dir="auto"`. Containers whose children carry `dir` get an
  explicit direction from their source.
- **Testing.** The snapshot tool's `direction` step (`"rtl"`, `"ltr"`, `"auto"`) and `print: "direction"` check
  this off-screen. Ctrl+Shift side keys can't be simulated there; arrow keys can (`key: "Right"`).
