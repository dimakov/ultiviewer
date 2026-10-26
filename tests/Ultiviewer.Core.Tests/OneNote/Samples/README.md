# OneNote test samples

- `OneWithFileData.one`: a OneNote 2016 section (one untitled page with an embedded `testing.docx`) from
  [OfficeDev/Interop-TestSuites](https://github.com/OfficeDev/Interop-TestSuites), MIT licensed (see `LICENSE`).
- `Notebook.onepkg`: a cabinet made with Windows' `makecab` (MSZIP, three CAB folders) holding
  `Big.txt` (4000 lines of `{i:D5} The quick brown fox jumps over the lazy dog {i % 97}\n`, 7 MSZIP blocks),
  `Notes.one` and `Group\Café.one` (both copies of `OneWithFileData.one`, the name in the ANSI code page) and
  `OneNote_RecycleBin\OneNote_DeletedPages.one` (one byte, `x`).
