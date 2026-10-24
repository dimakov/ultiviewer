# HLD: Formatted paste and web clipper

## What it does

### Formatted paste

Ctrl+V in a Markdown page turns HTML copied from a browser, Word or Google Docs into the Markdown the app writes
itself:

- headings and `**bold**`, `*italic*`, `~~strike~~`;
- `<u>`, `<sup>`, `<sub>` and `==highlight==`;
- links, resolved against the page they were copied from;
- nested, ordered and task lists, and quotes;
- fenced code with its language;
- tables, with alignment.

Pictures are handled by kind:

- Inline pictures (`data:` URIs) are saved in the page's `.assets` folder.
- Word's temporary `clip_image` files are copied there too.
- Web pictures keep their address.

Plain text is pasted as before in these cases:

- The HTML carries no formatting worth keeping. VS Code, for example, only colours spans.
- The text was copied from Ultiviewer's own editor.
- The caret is inside a code block.

Ctrl+Alt+V always pastes plain text. Settings → "Paste formatted text from browsers and Word as Markdown" turns the
feature off.

### Web clipper

File → **Clip web page…** asks for an address. It is prefilled from the clipboard when that holds a URL, and the path
of a saved `.html` file also works. The clipper downloads the page and keeps its readable part:

- **Title:** the article heading, or `og:title` / `<title>` without the site name.
- **Body:** the `<article>` / `<main>` with the most text, otherwise the element whose paragraphs hold the most
  text, weighted against link density.
- **Removed:** navigation, the site's header and footer, sidebars, comments, share and subscribe blocks, forms and
  scripts.

It then creates a page next to the selected page, or in the selected section:

```markdown
---
source: https://…
clipped: 2026-10-24 14:45
author: …
site: …
---

# Title

…article as Markdown…
```

Pictures are downloaded into `Title.assets/`, up to 60 per page, 4 at a time and 20 MB each. A picture that can't be
downloaded keeps its web address. The front matter makes clipped pages searchable with `source:*` or
`site:"Bread Blog"` (see `properties-and-saved-searches.md`).

## Main code

| File | Responsibility |
|---|---|
| `Core/Clipping/HtmlToMarkdown.cs` | The converter (AngleSharp DOM → Markdown); `MarkdownConversion.HasFormatting`; image callback. |
| `Core/Clipping/ClipboardHtml.cs` | CF_HTML: the fragment between `<!--StartFragment-->` / `<!--EndFragment-->` (or by byte offsets) and `SourceURL`. |
| `Core/Clipping/WebArticle.cs` | Title, byline and site; clutter removal; content scoring; `ToMarkdown(pagePath, now)` → Markdown + `ImageDownload`s. |
| `Core/Notebooks/FileNameRules.MakeValid`, `PageAssets.RelativeUrl` | Page names from titles; links to attachments. |
| `App/Views/EditorHost.Paste.cs` | Own-copy marker, `PasteFormatted` / `PasteHtml`, saving pasted pictures. |
| `App/Views/EditorHost.Tables.cs` | `InsertBlock`: inserts blocks with the blank lines they need (shared with pasted tables). |
| `App/Services/WebFetcher.cs` | HttpClient with redirects, decompression, a timeout and size limits; local files. |
| `App/ViewModels/MainViewModel.Clipping.cs` | `ClipWebPageCommand`, address parsing, page creation, picture downloads. |

## How it works

### Paste

`EditorHost.OnPreviewExecuted` handles Paste in Markdown documents in this order:

1. **Excel/TSV table** (`PasteTable`): cells become a table.
2. **Formatted paste:** when the setting is on, the clipboard has `DataFormats.Html`, the copy is not the app's own
   and the caret is not in a code fence, `ClipboardHtml.Parse` extracts the fragment and `HtmlToMarkdown`
   converts it. If `HasFormatting` is false, nothing is inserted and AvalonEdit pastes the plain text.
3. **AvalonEdit's text paste.**

Multi-line or block results (headings, lists, tables, fences, images) go through `InsertBlock`, which adds blank
lines around them. A single line of inline Markdown replaces the selection.

AvalonEdit puts syntax-coloured HTML on the clipboard when it copies. `OnCopying` adds an
`Ultiviewer.MarkdownSource` marker so pasting our own text stays plain.

### Converter rules worth knowing

- **Inline styles count.** Google Docs uses `font-weight:700` and `font-style:italic`, and wraps the whole fragment
  in `<b style="font-weight:normal">`. An inline element that contains blocks is treated as a container.
- **Word lists.** Word's `<p class=MsoListParagraph… style="mso-list:l0 level2">` paragraphs become a nested list.
  The marker span (`mso-list:Ignore`) decides between bullets and numbers and is dropped.
- **Breaks.** One `<br>` is a hard line break (`\` at the end of the line). Two breaks start a new paragraph.
- **Escaping.** Text that would turn into Markdown is escaped: `*`, `` ` ``, `[`, `]`, `$` (math), `_` at word
  edges, `~~`, `==`, `<tag`, and line starts such as `# `, `- `, `1. `, `>`.
- **Tables.** A table with a single cell or with block content (nested tables, lists) is treated as layout, and its
  cells become paragraphs. Otherwise it becomes a GitHub table with escaped pipes and `<br>` inside cells.
- **Images.** Lazy pictures use `data-src`, or the largest `srcset` entry, before a placeholder `src`.

### Clipper safety

- **Local files.** A web page can't make the clipper copy local files: `file:` pictures are only taken when the
  clipped page is itself a local file.
- **Scripts.** Scripts never run; the page is only parsed.
- **Limits.** Downloads are size-limited (10 MB per page, 20 MB per picture) and time out after 30 seconds.

## Extending safely

- **New HTML constructs.** Add them in `HtmlToMarkdown.Block` or `InlineElement`. Set `_formatted` (through `Wrap`
  or directly) when the result is formatting, so that plain-looking HTML still pastes as plain text. Add a case to
  `HtmlToMarkdownTests`.
- **Better article detection.** Change `WebArticle.RemoveClutter` and `PickContent`, and keep `WebArticleTests`
  green. `ToMarkdown` must keep returning every image it links to, so the app can download it.
- **Testing.** The snapshot tool's `pasteHtml` step pastes HTML without touching the real clipboard, and `clip`
  clips an address or `.html` file.
