# HLD: Markdown preview

## What it does

Shows a live, offline rendering of the active Markdown tab next to (Split), instead of (Preview) or not at all (Edit, and Live, which formats the editor itself — see `live-preview.md`) — per tab, cycled with **Ctrl+Shift+V** or the Live / Edit / Split / Preview buttons right of the tabs. The last choice becomes the default for newly opened pages.

- GitHub-flavoured Markdown plus tables, task lists, footnotes, front matter (hidden), math (KaTeX), Mermaid diagrams, highlighted code (highlight.js) and `[[wiki links]]`.
- A OneNote-like header: the page title (hidden when the page starts with its own `# H1`) and "Notebook › Section · edited date".
- Two-way scroll sync in Split mode.
- Clicking a task checkbox toggles `[ ]`/`[x]` in the source (an undoable edit; autosave writes it). Double-clicking a block jumps the editor to its source line.
- Links: `[[Title]]` → resolved page (offers to create a missing one); relative paths → opened in a tab (with `#heading` anchors), images/PDFs → default app, programs → confirmation first; `http(s)`/`mailto` → default browser/mail; `#anchor` → scrolls within the preview.

## Pipeline

```
editor edit ─► MainViewModel.DocumentEdited ─► EditorHost (debounce 180 ms) ─► Task.Run(MarkdownRenderer.ToHtml)
            ─► PreviewPane.Render(json) ─► PostWebMessageAsJson ─► preview.js: innerHTML swap, enhance(), scroll
```

- `Core/Markdown/MarkdownRenderer.cs` — Markdig pipeline (`UseAdvancedExtensions`, `UseYamlFrontMatter`, `WikiLinkExtension`). Before rendering, every block gets `data-line="<1-based source line>"`.
- `Core/Markdown/WikiLinkParser.cs` — inline parser for `[[target|label]]` → `<a class="wikilink" href="wiki:<escaped target>">`.
- `Core/Links/WikiLinkResolver.cs` — title or `Section/Title` → page path; prefers the same folder, then the same notebook, then the shortest path.
- `Core/Markdown/MarkdownHeadings.cs` — GitHub-style slugs, `#fragment` → heading line.
- `Core/Markdown/TaskMarkers.cs` — toggle/cycle task checkboxes on a line.
- `App/Preview/PreviewPane.cs` — the single WebView2: settings, virtual hosts, message bridge, navigation blocking, trimmed context menu.
- `App/Views/EditorHost.Preview.cs` — view-mode layout, render scheduling, scroll sync, double-click.
- `App/ViewModels/MainViewModel.Links.cs` — view-mode commands, `OpenLink`, wiki resolution/creation, `ToggleTaskAtLine`.
- `App/PreviewAssets/` (copied to `<app>\Preview\`) — `preview.html`, `preview.css`, `preview.js`, `vendor/` (see `vendor/NOTICE.md` for versions and licenses).

## Hosting and security contract

| Origin | Serves | How |
|---|---|---|
| `https://app.ultiviewer.example/` | the shell page, CSS, scripts, fonts | `SetVirtualHostNameToFolderMapping` to `<app>\Preview` (mapped before the first navigation) |
| `https://f<hash>.ultiviewer.example/` | files of one notebook (or the folder of an outside file) | `WebResourceRequested` handler: only files inside that folder, everything else 404 |

- The shell page loads once; `<base href>` is set per render to the document's folder URL so relative images and links resolve, including `../`.
- Virtual-host mappings added *after* the page loaded are not honoured by WebView2 — that is why notebook folders are served by the request handler instead.
- CSP: `script-src https://app.ultiviewer.example` only (no inline scripts, no `eval`), `style-src` app + inline (Mermaid SVG), `img-src https: http: data: blob:`, nothing else. HTML injected via `innerHTML` never runs `<script>`, and inline handlers like `onerror=` are blocked by the CSP.
- The page cannot navigate: every navigation except the shell URL is cancelled and treated as a link click; new windows are suppressed. Host objects are disabled; the only channel is JSON web messages. Browser accelerator keys are off, DevTools only with `ULTIVIEWER_DEVTOOLS=1`.

## Messages

| Direction | `type` | Fields |
|---|---|---|
| app → page | `render` | `html`, `baseUrl`, `title`, `meta`, `line` (fractional source line to scroll to, or null to keep position) |
| app → page | `scroll` | `line` |
| app → page | `theme` | `theme` (`light`/`dark`) — also sets `PreferredColorScheme` |
| page → app | `ready` | — (pending render/theme are flushed) |
| page → app | `scrolled` | `line` (fractional line at the top of the preview) |
| page → app | `link` | `href` (raw attribute value) |
| page → app | `toggleTask` | `line` |
| page → app | `dblclick` | `line` |

## Scroll sync

Line ↔ pixel mapping interpolates between the two nearest `[data-line]` elements. Editor → preview: on `TextView.ScrollOffsetChanged`, coalesced per frame. Preview → editor: the page reports its top line; the host scrolls the editor and ignores editor scroll events for 250 ms; the page ignores its own scroll events for 120 ms after a programmatic scroll. In Preview-only mode each tab remembers its preview line.

## Extending safely

- New Markdown syntax: add a Markdig extension in `Core/Markdown` and register it in `MarkdownRenderer.Pipeline`; keep `data-line` on blocks.
- New page features in `preview.js` must not need inline scripts or `eval` (CSP). Load big libraries lazily (Mermaid is only fetched when a diagram exists).
- New message types: add them to the tables above, `PreviewPane.OnWebMessage` and `preview.js`.
- Testing: run the app with `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9222` and evaluate JS through the DevTools protocol.
