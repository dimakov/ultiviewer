# HLD: OneNote import

## What it does

Ultiviewer imports OneNote notebooks into a new notebook folder of Markdown pages. It reads OneNote's files
itself, so OneNote doesn't need to be installed and the source is never opened by OneNote or changed.

Accepted inputs:

- a `.onepkg` package, which is what OneNote's *File → Export → Notebook* writes;
- a notebook folder, or its `Open Notebook.onetoc2`;
- a single `.one` section.

The result:

| OneNote | Ultiviewer |
|---|---|
| Notebook | A notebook folder, named after the package or folder (`Name 2` when taken) |
| Section group | A folder |
| Section | A section folder |
| Page | `Title.md`; front matter with `created`, `modified` and `source: OneNote`, then `# Title` |
| Subpage (level 2 and 3) | A page in the `Title/` folder next to its parent page |
| Pictures and attached files | `Title.assets/`, linked from the page |
| Order of sections and pages | The folders' manual order (`.ultiviewer.json`) |
| Created and modified times | The page file's creation and last-write times |

Page content:

- paragraphs, headings (h1–h6 styles), quotes, and consecutive *Code* paragraphs as one fenced block;
- nested bulleted and numbered lists, with restart numbers kept;
- to-do tags as `- [ ]` / `- [x]`; other tags as `#tags` (`Remember for later` → `#remember-for-later`);
- bold, italic, underline (`<u>`), strikethrough, sub/superscript, highlight (`==`), font color (`<span>`) and
  monospace fonts (`` `code` ``);
- web links; links to OneNote pages become `[[Page Title|text]]` and are re-pointed when the page file got a
  different name (`Q1: Plan` → `Q1 - Plan.md`);
- tables (first row as the header; several paragraphs in a cell are joined with `<br>`);
- pictures (alt text and picture links kept; the format is sniffed when OneNote didn't record it) and attached
  files. Printouts in EMF/WMF/XPS are linked as files, since browsers can't show them.

What can't be converted leaves a visible placeholder such as `*(a handwritten drawing (ink) not imported)*` and an
entry in the import report: ink drawings and handwriting inside text, equations (imported as plain text without
their structure), audio/video, and pictures or files whose data isn't in the file (cloud-only). Free positioning
of containers on the page is not kept: containers are read top to bottom, then left to right.

Not read yet: sections saved by OneNote for the web / OneDrive (the packaged FSSHTTPB variant). They are listed and
reported as "not imported" with that reason. Password-protected sections can't be read (their content is
encrypted); unlock them in OneNote before exporting. There is no sample of one yet, so how they show up is untested.

## How it works

```text
.onepkg ──Cabinet.Extract──► temp folder ─┐
folder / .onetoc2 ────────────────────────┼─► OneNoteSource ─► OneNoteNotebook (groups, OneNoteSectionFile)
.one ─────────────────────────────────────┘                          │
                                                                     ▼  per section, when needed
                          RevisionStoreReader (MS-ONESTORE) ─► OneStoreFile ─► OneNoteReader (MS-ONE)
                                                                     │
                                                                     ▼
                                     OneNoteSection ─► OneNotePage ─► OneNoteMarkdown.Convert
                                                                     │       (Markdown, assets, notes)
                                                                     ▼
                                     OneNoteImporter ─► staging folder ─► moved to the destination
```

### Files

| File | Owns |
|---|---|
| `Core/OneNote/Cabinet.cs` | CAB extraction for `.onepkg`. MSZIP blocks are inflated by `DeflateStream` after a stored deflate block that holds the previous 32 KB (MSZIP's history between blocks; `DeflateStream` can't be given a dictionary). Other compression goes to Windows' `tar.exe`. Entry names that leave the destination are skipped. |
| `Core/OneNote/OneNoteSource.cs` | Opens an input, lists section groups and sections (`.onetoc2` order first, then by name; `OneNote_RecycleBin` skipped), and deletes a package's temporary folder on `Dispose`. |
| `Core/OneNote/Store/RevisionStoreReader.cs` | The desktop revision store: header, transaction log (committed node counts per list), file node lists, object spaces and their active revisions (role 1, default context, dependency chains), object groups and their global ID tables, property sets, the file data store and `<ifndf>`/`<file>`/`<invfdo>` references (`<file>` is looked up next to the section and in `onefiles/`). |
| `Core/OneNote/Store/StoreModel.cs` | The format-neutral result: object spaces of `StoreObject`s (JCID + `PropertySet`), with typed property accessors. A future FSSHTTPB reader produces the same model. |
| `Core/OneNote/Store/OneStoreLoader.cs` | Picks the reader by the format GUID in the header. |
| `Core/OneNote/OneNoteReader.cs` | The content model on top of the store: sections → pages (metadata space for title, level and times; content space for the page tree), outlines, paragraphs, runs, styles, tags, pictures, files, tables; the `.onetoc2` order. JCIDs and property IDs are constants at the top. |
| `Core/OneNote/OneNoteModel.cs` | The page model (`OneNotePage`, `OneNoteOutline`, `OneNoteElement`, `OneNoteText`/`OneNoteRun`/`OneNoteStyle`, `OneNoteImage`, `OneNoteFile`, `OneNoteTable`, `OneNoteUnsupported`). |
| `Core/OneNote/OneNoteMarkdown.cs` | Page → Markdown + assets + notes. Uses `Core/Markdown/MarkdownEscape.cs` (shared with formatted paste) so text never turns into Markdown by accident. |
| `Core/OneNote/OneNoteImporter.cs` | Writes the notebook: arranges subpages (`Arrange`), applies `OneNoteImportOptions` (sections, pages, a section cache), writes pages, assets, order and file times, re-points page links, and returns `OneNoteImportResult` with the issues. |

### Contracts

- **Never modifies the source.** Files are only read; packages are extracted to `%TEMP%\Ultiviewer\onepkg-*`.
- **All or nothing.** The notebook is written to `.uv-import-*` inside the destination folder and moved into place
  when complete. Cancelling (the token is checked between pages) or an error deletes the staging folder.
- **Names.** Folder and page names go through `FileNameRules.MakeValid` (`:` → ` - `, reserved names avoided) and
  get ` 2`, ` 3`… when taken in their folder. Empty titles become "Untitled page".
- **Selection.** `IncludePage` works on indexes into `OneNoteSection.Pages`. Leaving out a page whose subpages are
  chosen moves those subpages up into its place. A section group whose sections all fail is not created.
- **Report.** Each `OneNoteImportIssue` names "Group › Section › Page" and what was lost; identical notes on a page
  are counted (`(×4)`).

## Extending it safely

- **New content type:** add its JCID and properties to `OneNoteReader`, a record to `OneNoteModel`, and a case in
  `OneNoteMarkdown.Writer.Content`. Anything that can't be represented should become `OneNoteUnsupported` with a
  human name, so it is reported instead of silently dropped.
- **OneDrive (FSSHTTPB) sections:** write a reader that turns the packaged storage into `OneStoreFile` (object
  spaces of `StoreObject`s with the same property sets) and select it in `OneStoreLoader`. Nothing above the store
  needs to change.
- **Markdown output:** keep escaping through `MarkdownEscape`; formatting must not span line breaks (each line is
  formatted on its own) and must wrap text without its surrounding spaces (`Hoist`), or CommonMark won't see it.
- Tests: `tests/Ultiviewer.Core.Tests/OneNote/`. Only the MIT sample `OneWithFileData.one` (with its `LICENSE`)
  and a `makecab`-built package of it are committed; other public samples are under incompatible licenses.
