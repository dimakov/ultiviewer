# Third-party notices

Ultiviewer is MIT licensed (see `LICENSE`). It uses, and its release build ships, the components below under
their own licenses.

## NuGet packages

| Package | Version | License | Used for |
|---|---|---|---|
| [AvalonEdit](https://github.com/icsharpcode/AvalonEdit) | 6.3.1.120 | MIT | The text editor |
| [Markdig](https://github.com/xoofx/markdig) | 1.4.2 | BSD-2-Clause | Markdown parsing and rendering |
| [AngleSharp](https://github.com/AngleSharp/AngleSharp) | 1.8.4 | MIT | HTML parsing (formatted paste, web clipper) |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | 8.4.2 | MIT | View models |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) | 10.0.13 | MIT | The search index; brings [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) (Apache-2.0) and [SQLite](https://sqlite.org/copyright.html) (public domain) |
| [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2) | 1.0.4314.35 | BSD-3-Clause style (Microsoft) | The preview |
| [PdfPig](https://github.com/UglyToad/PdfPig) | 0.1.16 | Apache-2.0 | Text inside PDF attachments, for search |
| [.NET runtime](https://github.com/dotnet/runtime) and [WPF](https://github.com/dotnet/wpf) | 10.0 | MIT | The self-contained release build |

Tests only (not shipped): [xUnit](https://github.com/xunit/xunit) (Apache-2.0) and
[Microsoft.NET.Test.Sdk](https://github.com/microsoft/vstest) (MIT).

## Preview libraries

[highlight.js](https://highlightjs.org/) (BSD-3-Clause), [Mermaid](https://mermaid.js.org/) (MIT) and
[KaTeX](https://katex.org/) (MIT) are bundled in `src/Ultiviewer.App/PreviewAssets/vendor`; versions, files and
local edits are listed in `vendor/NOTICE.md`.

## OneNote import

- The OneNote readers were written from Microsoft's published Open Specifications: [MS-ONESTORE], [MS-ONE],
  [MS-FSSHTTPB] and [MS-CAB].
- The [onenote.rs](https://github.com/msiemens/onenote.rs) parser (MPL-2.0) was used as a reference for format
  details and to check results against its public sample files. No sample files from it are included.
- `tests/Ultiviewer.Core.Tests/OneNote/Samples/OneWithFileData.one` comes from
  [OfficeDev/Interop-TestSuites](https://github.com/OfficeDev/Interop-TestSuites) (MIT); its license is next to it.
