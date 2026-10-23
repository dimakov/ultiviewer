# Third-party preview libraries

Bundled unmodified (except as noted) so the Markdown preview works offline. Downloaded from cdnjs.

| Library | Version | License | Files |
|---|---|---|---|
| [highlight.js](https://highlightjs.org/) | 11.11.1 | BSD-3-Clause | `highlight/highlight.min.js`, `highlight/github.min.css`, `highlight/github-dark.min.css` |
| [Mermaid](https://mermaid.js.org/) | 11.12.0 | MIT | `mermaid/mermaid.min.js` |
| [KaTeX](https://katex.org/) | 0.16.21 | MIT | `katex/katex.min.js`, `katex/katex.min.css`, `katex/contrib/auto-render.min.js`, `katex/fonts/*.woff2` |

`katex.min.css` was edited to reference only the `.woff2` fonts (the `.woff`/`.ttf` fallbacks are not shipped).

To update: download the same file set for the new version, keep the KaTeX CSS edit, update this table, and check the preview still renders code, math and a Mermaid diagram.
