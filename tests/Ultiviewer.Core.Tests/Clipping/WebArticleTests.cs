using Ultiviewer.Core.Clipping;
using Ultiviewer.Core.Notebooks;

namespace Ultiviewer.Core.Tests.Clipping;

public class WebArticleTests
{
    private const string Page = """
        <!doctype html>
        <html><head>
          <title>How to bake bread | Kitchen Notes</title>
          <meta property="og:site_name" content="Kitchen Notes">
          <meta name="author" content="Dana Cook">
        </head><body>
          <header><nav><a href="/">Home</a> <a href="/about">About</a></nav></header>
          <div class="sidebar"><p>Popular: cakes</p></div>
          <article>
            <h1>How to bake bread</h1>
            <p>Bread needs flour, water, salt and yeast. Mix them, knead for ten minutes, and let the dough rise.</p>
            <figure><img src="img/loaf.jpg" alt="A loaf"><figcaption>Our loaf</figcaption></figure>
            <p>Bake at 230 degrees for about forty minutes, until the crust is deep brown and sounds hollow.</p>
            <div class="share-buttons"><a href="#">Share</a></div>
          </article>
          <div id="comments"><p>Great post!</p></div>
          <footer><p>© Kitchen Notes</p></footer>
          <script>track()</script>
        </body></html>
        """;

    [Fact]
    public void FindsTheArticleTitleAndByline()
    {
        var article = WebArticle.Parse(Page, new Uri("https://kitchen.test/bread/how-to"));

        Assert.Equal("How to bake bread", article.Title);
        Assert.Equal("Dana Cook", article.Byline);
        Assert.Equal("Kitchen Notes", article.SiteName);
    }

    [Fact]
    public void WritesFrontMatterArticleAndImageDownloads()
    {
        var article = WebArticle.Parse(Page, new Uri("https://kitchen.test/bread/how-to"));
        var page = Path.Combine("C:\\nb", "How to bake bread.md");

        var result = article.ToMarkdown(page, new DateTime(2026, 10, 24, 9, 30, 0));

        Assert.Equal("""
            ---
            source: https://kitchen.test/bread/how-to
            clipped: 2026-10-24 09:30
            author: Dana Cook
            site: Kitchen Notes
            ---

            # How to bake bread

            Bread needs flour, water, salt and yeast. Mix them, knead for ten minutes, and let the dough rise.

            ![A loaf](How%20to%20bake%20bread.assets/loaf.jpg)

            *Our loaf*

            Bake at 230 degrees for about forty minutes, until the crust is deep brown and sounds hollow.

            """.Replace("\r\n", "\n"), result.Markdown);
        var image = Assert.Single(result.Images);
        Assert.Equal(new Uri("https://kitchen.test/bread/img/loaf.jpg"), image.Source);
        Assert.Equal(Path.Combine(PageAssets.FolderFor(page), "loaf.jpg"), image.TargetPath);
    }

    [Fact]
    public void FallsBackToTheParagraphRichestElement()
    {
        const string html = """
            <html><head><title>Plain</title></head><body>
              <div class="menu"><a href="/a">A</a><a href="/b">B</a></div>
              <div class="text">
                <p>First paragraph of the story, long enough to count as real content for the scorer.</p>
                <p>Second paragraph, with commas, clauses, and more words to make it the obvious winner here.</p>
              </div>
              <div class="links"><p><a href="/x">A very long link text that is only navigation and nothing else at all</a></p></div>
            </body></html>
            """;

        var markdown = WebArticle.Parse(html, new Uri("https://plain.test/")).ToMarkdown("C:\\nb\\Plain.md", DateTime.Now).Markdown;

        Assert.Contains("First paragraph of the story", markdown);
        Assert.Contains("Second paragraph", markdown);
        Assert.DoesNotContain("navigation", markdown);
        Assert.DoesNotContain("[A]", markdown);
    }

    [Theory]
    [InlineData("Breaking: News | Site", "Breaking - News - Site")]
    [InlineData("What? *Really* <yes>", "What Really yes")]
    [InlineData("  ...  ", "Clipped page")]
    [InlineData("CON", "Clipped page")]
    public void MakesValidPageNames(string title, string expected)
    {
        Assert.Equal(expected, FileNameRules.MakeValid(title, "Clipped page"));
    }
}
