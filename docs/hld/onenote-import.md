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

Both of OneNote's section formats are read: the desktop revision store (OneNote 2016 and the desktop app's
exports) and the packaged FSSHTTPB store that OneNote for the web and OneDrive write (also inside a desktop-style
header in newer downloads). Untitled pages are named after their first line of text, as OneNote lists them.
Password-protected sections can't be read (their content is encrypted); unlock them in OneNote before exporting.
There is no sample of one yet, so how they show up is untested.

## The import window

File → **Import from OneNote…** (or dropping a `.onepkg`, `.one` or `.onetoc2` on the main window) opens the file
on a background thread and shows the import window:

- **Left:** section groups, sections and pages (subpages nested) with check boxes. Checking a row checks
  everything under it; groups and sections show the combined state (indeterminate when mixed); a page's box is
  independent of its subpages'. Each section shows its page count, and ⚠ marks pages with conversion notes and
  sections that can't be read (those start unchecked).
- **Right:** the selected page as it will be written: **Preview** (the normal WebView2 preview, with pictures from
  a temporary folder) and **Markdown**, with "Not imported exactly" notes underneath.
- **Bottom:** notebook name and destination folder (default: the app's notebooks folder), the selection summary,
  progress, **Import** and **Close**. After an import the notes area shows the report, and the new notebook is
  opened in the tree.

All sections are read once in the background when the window opens (for counts and notes); the last four read
are kept in memory and reused by the preview and the import. Closing the window cancels reading or importing,
waits for it, and deletes the preview folder and the package's temporary folder.

| File | Owns |
|---|---|
| `App/ViewModels/MainViewModel.OneNote.cs` | The command: pick or receive a file, `OneNoteSource.Open` on a background thread, raise `OneNoteImportRequested`, open the imported notebook. |
| `App/ViewModels/OneNoteImportViewModel.cs` | The tree (`OneNoteImportItem`: check-state propagation), background scan, section cache, page preview (converted with the same asset naming as the import), and the import with the choices snapshotted into `OneNoteImportOptions`. |
| `App/Views/OneNoteImportWindow.xaml(.cs)` | Layout, the `PreviewPane`, and close handling. |
| `tools/Ultiviewer.Snapshot` (`oneNote` step) | Opens the window off-screen, prints the tree, unchecks/selects by path, saves the window, the rendered page (`PreviewPane.CaptureAsync`) and the Markdown tab, and imports. |

## How it works

```text
.onepkg ──Cabinet.Extract──► temp folder ─┐
folder / .onetoc2 ────────────────────────┼─► OneNoteSource ─► OneNoteNotebook (groups, OneNoteSectionFile)
.one ─────────────────────────────────────┘                          │
                                                                     ▼  per section, when needed
      RevisionStoreReader (desktop) or PackageStoreReader (web) ─► OneStoreFile ─► OneNoteReader (MS-ONE)
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
| `Core/OneNote/Store/PackageStoreReader.cs` | The packaged store of OneNote for the web ([MS-ONESTORE] 2.8 around [MS-FSSHTTPB]): stream objects and data elements (storage index, storage manifest, cell and revision manifests, object groups, data blobs). Each cell (context + object space) becomes an object space; a revision's objects come first, then its base revisions' other objects. Objects are split in partitions: 4 holds the JCID, 1 the property set (its compact IDs resolve in order through the object's references and cells), 2 a blob with the file data. |
| `Core/OneNote/Store/PropertySetReader.cs` | The ObjectSpaceObjectPropSet structure both readers share; each reader says how compact IDs become ids. |
| `Core/OneNote/Store/StoreModel.cs` | The format-neutral result: object spaces of `StoreObject`s (JCID + `PropertySet`), with typed property accessors. |
| `Core/OneNote/Store/OneStoreLoader.cs` | Picks the reader by the format GUID in the header, and finds a package embedded after a desktop header. |
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
- **Store formats:** both readers end in the same `OneStoreFile`, so the content model never knows the format.
  In the packaged store, one object space can exist in several contexts (a page's content and its version
  history); spaces are keyed by context and space together (`CellId.Key`), or a version history can hide a page.
  Use the storage index the package header names; older ones can still be in the file.
- **Markdown output:** keep escaping through `MarkdownEscape`; formatting must not span line breaks (each line is
  formatted on its own) and must wrap text without its surrounding spaces (`Hoist`), or CommonMark won't see it.
- Tests: `tests/Ultiviewer.Core.Tests/OneNote/`. Only the MIT sample `OneWithFileData.one` (with its `LICENSE`)
  and a `makecab`-built package of it are committed; other public samples are under incompatible licenses.
  `PackageStoreTests` builds a small FSSHTTPB package in code (revisions, a version-history context, a picture
  blob, the embedded variant).
