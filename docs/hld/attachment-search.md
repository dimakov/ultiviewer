# HLD: Search inside PDFs and pictures

## What it does

Notebook search (Ctrl+Shift+F) also finds text inside **PDFs** and **pictures** stored anywhere in a notebook,
including pages' `Title.assets` folders (hidden folders such as `.git` are skipped):

- PDF text layers are read with PdfPig.
- With OCR on, pictures (`.png .jpg .jpeg .bmp .gif .tif .tiff .webp`) and PDF pages without text (scans) are
  read with Windows' built-in text recognition (`Windows.Media.Ocr`, in the user's Windows languages, falling back
  to English). Scanned PDF pages are rendered with `Windows.Data.Pdf` at about 200 dpi first.
- Results show a paper clip, the file name, and per page `p. N` with a snippet around the match. Clicking opens
  the file in its default app; the status bar names the page.
- Settings → Search: *Search text inside PDFs and pictures* (`searchAttachments`) and *Recognize text in pictures
  and scanned PDFs* (`ocrImages`), both on by default.

## How it works

```
notebooks opened / attachment changed ─► RequestAttachmentIndexing ─► background thread (below normal priority)
   └► NotebookIndex.UpdateAttachments(reader, max 5 files per call)
        lock: list known attachments, drop deleted ones
        no lock: reader.Read(file)   (PdfPig; OCR via WindowsAttachmentReader)
        lock: store Pages(kind = 1) + Blocks (folded, line = page) + AttachmentPages (original text)
search ─► Candidates (pages and attachments) ─► SearchService.EvaluateAttachment (stored text, one line per page)
```

- Extraction never runs inside a search: searches only see what has been read so far, and the reader is resumed
  whenever notebooks change and every 10 minutes.
- A file that can't be read is stored without text, so it isn't retried until its size or time changes.
- Page indexing (`Update`, `PageCount`) ignores attachment rows; deleting a file removes its row on the next pass.

## Main code

| File | Responsibility |
|---|---|
| `Core/Search/Attachments.cs` | `IAttachmentReader`, `AttachmentFiles` (which files, enumeration), `PdfTextReader` (PdfPig). |
| `Core/Search/NotebookIndex.cs` | Schema 2, `UpdateAttachments`, `AttachmentText`, `RemoveAttachments`, `Candidates(includeAttachments)`. |
| `Core/Search/SearchService.cs` | `EvaluateAttachment`, `Snippet`, `IncludeAttachments`; `PageResult.IsAttachment`. |
| `App/Services/WindowsAttachmentReader.cs` | PDF text + OCR of pictures and scanned pages. |
| `App/ViewModels/MainViewModel.Attachments.cs` | The background thread. |
| `App/ViewModels/SearchResultViewModels.cs`, `App/Views/SearchPanel.xaml` | `p. N`, paper clip, open with the default app. |

The App targets `net10.0-windows10.0.19041.0` (Windows 10 2004 or later) for the WinRT OCR and PDF APIs.

## Extending safely

- More formats (Word, PowerPoint): implement `IAttachmentReader`, add the extensions to `AttachmentFiles`, and
  combine readers in the app.
- Keep readers free of UI and thread affinity; they run on the background thread.
- A schema change bumps `NotebookIndex.SchemaVersion` (indexes rebuild; attachments are read again).
