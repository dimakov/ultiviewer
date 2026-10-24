using Ultiviewer.Core.Markdown;

namespace Ultiviewer.Core.Tests.Markdown;

public class HtmlExportTests
{
    [Fact]
    public void InlinesLocalImagesAndKeepsWebImages()
    {
        using var temp = new TempDirectory();
        temp.Folder("Page.assets");
        File.WriteAllBytes(temp.Combine("Page.assets/dot.png"), [0x89, 0x50, 0x4E, 0x47]);

        var html = HtmlExport.InlineImages("<img src=\"Page.assets/dot.png\" alt=\"x\"><img src=\"https://example.com/a.png\"><img src=\"missing.png\">", temp.Path);

        Assert.Contains("src=\"data:image/png;base64,iVBORw==\"", html, StringComparison.Ordinal);
        Assert.Contains("src=\"https://example.com/a.png\"", html, StringComparison.Ordinal);
        Assert.Contains("src=\"missing.png\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TurnsPageBreakMarkersIntoBreaks()
    {
        var html = HtmlExport.PageBreaks(MarkdownRenderer.ToHtml("one\n\n<!-- pagebreak -->\n\ntwo\n\n\\newpage\n\nthree"));

        Assert.Equal(2, html.Split("class=\"page-break\"").Length - 1);
    }

    [Fact]
    public void ComposesSectionsWithTitlesAnchorsAndInternalLinks()
    {
        var html = HtmlExport.Compose(
            [new ExportPage("First", "See [[Second]] and [[Elsewhere]] #tag", null), new ExportPage("Second", "# Own title\n\ntext", null)],
            titlePages: true,
            pageBreakBetween: true);

        Assert.Contains("<section class=\"export-page\" id=\"page-first\"><h1>First</h1>", html, StringComparison.Ordinal);
        Assert.Contains("<a href=\"#page-second\" class=\"wikilink\">Second</a>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"wikilink\">Elsewhere</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tag\">#tag</span>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<h1>Second</h1>", html, StringComparison.Ordinal); // it has its own title
        Assert.Single(html.Split("page-break")[1..]);
    }

    [Fact]
    public void BuildsAStandaloneDocumentWithEmbeddedFonts()
    {
        using var temp = new TempDirectory();
        temp.Folder("fonts");
        File.WriteAllBytes(temp.Combine("fonts/A.woff2"), [1, 2, 3]);
        var css = HtmlExport.EmbedFonts("@font-face{font-family:A;src:url(fonts/A.woff2) format(\"woff2\")}", temp.Path);
        var document = HtmlExport.Document("My <page>", "<p>body</p>", [css]);

        Assert.Contains("src:url(data:font/woff2;base64,AQID) format(\"woff2\")", css, StringComparison.Ordinal);
        Assert.Contains("<title>My &lt;page&gt;</title>", document, StringComparison.Ordinal);
        Assert.Contains("<main id=\"content\" class=\"markdown-body\"><p>body</p></main>", document, StringComparison.Ordinal);
    }
}
