using Ultiviewer.Core.Search;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Ultiviewer.Core.Tests.Search;

public class AttachmentSearchTests
{
    /// <summary>Pretends to OCR images: their "text" is a fixed sentence; counts reads.</summary>
    private sealed class FakeReader : IAttachmentReader
    {
        public int Reads { get; private set; }

        public bool CanRead(string path) => AttachmentFiles.IsAttachment(path);

        public IReadOnlyList<string> Read(string path, CancellationToken cancellationToken)
        {
            Reads++;
            return Path.GetExtension(path) == ".pdf" ? new PdfTextReader().Read(path, cancellationToken) : ["whiteboard: quarterly roadmap and hiring"];
        }
    }

    private static void WritePdf(string path, params string[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pages)
        {
            builder.AddPage(PageSize.A4).AddText(text, 12, new UglyToad.PdfPig.Core.PdfPoint(40, 700), font);
        }

        File.WriteAllBytes(path, builder.Build());
    }

    [Fact]
    public void ReadsPdfTextPerPage()
    {
        using var dir = new TempDirectory();
        WritePdf(dir.Combine("doc.pdf"), "Invoice number 4711", "Total due 99 EUR");

        Assert.Equal(["Invoice number 4711", "Total due 99 EUR"], new PdfTextReader().Read(dir.Combine("doc.pdf"), CancellationToken.None));
    }

    [Fact]
    public async Task FindsTextInPdfsAndImagesWithPageNumbers()
    {
        using var dir = new TempDirectory();
        var nb = dir.Folder("nb");
        dir.File("nb/Meeting.md", "# Meeting\n\nsee the attached invoice");
        dir.Folder("nb/Meeting.assets");
        WritePdf(dir.Combine("nb/Meeting.assets/scan.pdf"), "Cover page", "Invoice number 4711 for consulting");
        File.WriteAllBytes(dir.Combine("nb/Meeting.assets/board.png"), [1, 2, 3]);
        dir.Folder("nb/.git");
        File.WriteAllBytes(dir.Combine("nb/.git/ignored.png"), [1]);

        using var index = NotebookIndex.Open(nb, dir.Combine("index"));
        index.Update();
        var reader = new FakeReader();
        Assert.Equal((2, 0), index.UpdateAttachments(reader));
        Assert.Equal((0, 0), index.UpdateAttachments(reader)); // nothing changed
        Assert.Equal(2, reader.Reads);
        Assert.Equal((0, 0), index.Update()); // page updates leave attachments alone
        Assert.Equal(1, index.PageCount());

        using var service = new SearchService(dir.Combine("index"));
        service.SetNotebooks([nb]);
        var results = new List<PageResult>();
        await foreach (var result in service.SearchAsync("invoice 4711", SearchScope.Everywhere, new Dictionary<string, string>()))
        {
            results.Add(result);
        }

        var pdf = Assert.Single(results, r => r.IsAttachment);
        Assert.Equal("scan.pdf", pdf.Title);
        Assert.Equal([2, 2], pdf.Hits.Select(h => h.Line)); // both words, on page 2
        Assert.Equal(["Invoice", "4711"], pdf.Hits.Select(h => h.LineText.Substring(h.Column, h.Length)));

        results.Clear();
        await foreach (var result in service.SearchAsync("roadmap", SearchScope.Everywhere, new Dictionary<string, string>()))
        {
            results.Add(result);
        }

        Assert.Equal("board.png", Assert.Single(results).Title);

        service.IncludeAttachments = false;
        results.Clear();
        await foreach (var result in service.SearchAsync("roadmap", SearchScope.Everywhere, new Dictionary<string, string>()))
        {
            results.Add(result);
        }

        Assert.Empty(results);
    }

    [Fact]
    public void ForgetsDeletedAndRereadsChangedAttachments()
    {
        using var dir = new TempDirectory();
        var nb = dir.Folder("nb");
        File.WriteAllBytes(dir.Combine("nb/a.png"), [1]);
        File.WriteAllBytes(dir.Combine("nb/b.png"), [1]);
        using var index = NotebookIndex.Open(nb, dir.Combine("index"));
        var reader = new FakeReader();
        index.UpdateAttachments(reader);

        File.Delete(dir.Combine("nb/a.png"));
        File.WriteAllBytes(dir.Combine("nb/b.png"), [1, 2]);
        File.SetLastWriteTimeUtc(dir.Combine("nb/b.png"), DateTime.UtcNow.AddMinutes(1));

        Assert.Equal((1, 0), index.UpdateAttachments(reader));
        Assert.Null(index.AttachmentText(dir.Combine("nb/a.png")));
        Assert.Equal(["whiteboard: quarterly roadmap and hiring"], index.AttachmentText(dir.Combine("nb/b.png")));
    }

    [Fact]
    public void SnippetsCutLongPagesAroundTheMatch()
    {
        var text = new string('a', 200) + "MATCH" + new string('b', 200);
        var hit = SearchService.Snippet(3, 200, 5, text);

        Assert.StartsWith("…", hit.LineText, StringComparison.Ordinal);
        Assert.EndsWith("…", hit.LineText, StringComparison.Ordinal);
        Assert.Equal("MATCH", hit.LineText.Substring(hit.Column, 5));
        Assert.Equal(3, hit.Line);
    }
}
