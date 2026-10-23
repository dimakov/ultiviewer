using Ultiviewer.Core.Links;
using Ultiviewer.Core.Markdown;
using Ultiviewer.Core.Notebooks;

namespace Ultiviewer.Core.Tests.Markdown;

public class ConveniencesTests
{
    [Fact]
    public void TagsRenderAsChipsButNotInCodeOrUrls()
    {
        var html = MarkdownRenderer.ToHtml("Plan #home/Reno and `#code` and https://x.com/#frag and C#\n");

        Assert.Contains("<a href=\"tag:home%2Freno\" class=\"tag\">#home/Reno</a>", html);
        Assert.DoesNotContain("tag:code", html);
        Assert.DoesNotContain("tag:frag", html);
        Assert.Contains("C#", html);
    }

    [Theory]
    [InlineData("- item", "- ", false)]
    [InlineData("  * nested", "  * ", false)]
    [InlineData("9. ninth", "10. ", false)]
    [InlineData("3) third", "4) ", false)]
    [InlineData("- [x] done task", "- [ ] ", false)]
    [InlineData("> quoted", "> ", false)]
    [InlineData("- ", null, true)]
    [InlineData("- [ ] ", null, true)]
    [InlineData("plain", null, false)]
    public void ContinuesLists(string line, string? prefix, bool ends)
    {
        Assert.Equal(new EnterAction(prefix, ends), ListContinuation.OnEnter(line));
    }

    [Fact]
    public void RewritesLinksAfterAPageMovesToAnotherSection()
    {
        using var dir = new TempDirectory();
        var oldPage = dir.Combine("A/Plan.md");
        var newPage = dir.Combine("B/Sub/Plan.md");
        var moves = new List<PathMove>
        {
            new(oldPage, newPage),
            new(dir.Combine("A/Plan.assets"), dir.Combine("B/Sub/Plan.assets")),
        };

        // A page linking to the moved page.
        var (inbound, inboundCount) = LinkUpdater.Rewrite(
            "See [plan](A/Plan.md#goals), [[Plan]] and [other](A/Other.md).", dir.Combine("Index.md"), moves);
        Assert.Equal("See [plan](B/Sub/Plan.md#goals), [[Plan]] and [other](A/Other.md).", inbound);
        Assert.Equal(1, inboundCount);

        // The moved page's own links are rebased; its attachments moved along.
        var (own, ownCount) = LinkUpdater.Rewrite("![i](Plan.assets/x.png) [o](Other.md) [up](../Index.md)", newPage, moves);
        Assert.Equal("![i](Plan.assets/x.png) [o](../../A/Other.md) [up](../../Index.md)", own);
        Assert.Equal(2, ownCount);
    }

    [Fact]
    public void RewritesWikiLinksAndFolderMoves()
    {
        using var dir = new TempDirectory();
        var moves = new List<PathMove> { new(dir.Combine("Work"), dir.Combine("Job")), new(dir.Combine("Job/Old.md"), dir.Combine("Job/New.md")) };

        var (text, count) = LinkUpdater.Rewrite("[[Old]] [[Work/Old|label]] [x](Work/Old.md) [y](Work/Keep.md)", dir.Combine("Home.md"), moves, "Old", "New");

        Assert.Equal("[[New]] [[Work/New|label]] [x](Job/New.md) [y](Job/Keep.md)", text);
        Assert.Equal(4, count);
    }

    [Fact]
    public void AssetsAndTemplates()
    {
        using var dir = new TempDirectory();
        var page = dir.Combine("My Page.md");
        var folder = PageAssets.FolderFor(page);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "shot.png"), "");

        Assert.Equal(dir.Combine("My Page.assets"), folder);
        Assert.Equal(Path.Combine(folder, "shot 2.png"), PageAssets.UniquePath(folder, "shot.png"));
        Assert.Equal("![shot](My%20Page.assets/shot.png)", PageAssets.MarkdownLink(page, Path.Combine(folder, "shot.png")));
        Assert.Equal("[report.pdf](My%20Page.assets/report.pdf)", PageAssets.MarkdownLink(page, Path.Combine(folder, "report.pdf")));

        var now = new DateTime(2026, 10, 23, 14, 5, 0);
        Assert.Equal("# Standup\n2026-10-23 14:05", PageTemplates.Apply("# {{title}}\n{{datetime}}", "Standup", now, "yyyy-MM-dd", "HH:mm"));
    }
}
