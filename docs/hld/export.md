# HLD: HTML / PDF export and printing

## What it does

**File → Export page as HTML or PDF…** (Ctrl+Shift+E), **File → Print…** (Ctrl+P), and *Export as HTML or PDF…*
on a page, section or notebook in the tree open an export window that is also the print preview:

- The document is rendered like the preview (light theme): code colors, KaTeX math and Mermaid diagrams are drawn.
- **Save as HTML…** writes one self-contained file: the preview's CSS, code colors and (when math is present)
  KaTeX's CSS with its fonts embedded, images as data URIs. It opens anywhere without Ultiviewer.
- **Save as PDF…** prints to PDF with the chosen paper (A4 / Letter), orientation and margins.
- **Print…** opens the printer dialog (Print from the menu opens the window and the dialog together).
- A section or notebook exports every page in tree order, each page as a section titled with its page name
  (unless it starts with its own `#` heading), optionally each on a new sheet; `[[wiki links]]` to exported pages
  jump inside the document, other wiki links and `#tags` become plain text.
- `<!-- pagebreak -->`, `\newpage` and `\pagebreak` start a new printed page (and show as a dashed line in the
  preview). Print CSS keeps code blocks, tables, images and diagrams in one piece and headings with their text.
- Obsidian's image sizes (`![alt|300](img.png)`, `|300x200`) apply in the preview and exports too.

## Main code

| File | Responsibility |
|---|---|
| `Core/Markdown/HtmlExport.cs` | `Compose` (pages → HTML sections), `InlineImages`, `PageBreaks`, `ResolveLinks`, `Document`, `EmbedFonts`. |
| `Core/Markdown/MarkdownRenderer.cs` | Image sizes from alt text. |
| `App/ViewModels/MainViewModel.Export.cs` | `ExportRequest` for the active tab, a page or a section (open tabs' unsaved text is used). |
| `App/Views/ExportWindow.xaml(.cs)` | Render once, `SaveHtmlAsync`, `SavePdfAsync`, print. |
| `App/Preview/PreviewPane.cs`, `PreviewAssets/preview.js`, `preview.css` | `rendered` message after diagrams are drawn, `GetContentHtmlAsync`, `PrintToPdfAsync`, `ShowPrintDialog`; page-break and print CSS. |

## Design notes

- Rendering happens in the browser once; HTML export saves what was drawn (so diagrams are SVG and math is
  KaTeX HTML) instead of shipping the JavaScript libraries.
- Images are inlined before rendering, which keeps the preview's strict CSP (data: images are allowed) and makes
  the saved HTML independent of the notebook folders.
- Saves and prints wait for the render to finish; a wait that started before a re-render keeps waiting for it
  (the completion source is only replaced once finished).

## Extending safely

- New export formats that need the rendered DOM can reuse `ExportWindow` (`GetContentHtmlAsync`).
- Anything the preview shell renders client-side must signal completion before `rendered` is posted.
