using Ultiviewer.Core.Links;
using Ultiviewer.Core.Markdown;

namespace Ultiviewer.Core.Tests.Markdown;

public class MarkdownRendererTests
{
    [Fact]
    public void BlocksCarrySourceLines()
    {
        var html = MarkdownRenderer.ToHtml("# Title\n\nPara one\n\n- a\n- [x] done\n");

        Assert.Contains("<h1 id=\"title\" data-line=\"1\">Title</h1>", html);
        Assert.Contains("<p data-line=\"3\">Para one</p>", html);
        Assert.Contains("data-line=\"6\"", html);
        Assert.Contains("type=\"checkbox\"", html);
    }

    [Fact]
    public void RendersTablesMathMermaidAndSkipsFrontMatter()
    {
        var html = MarkdownRenderer.ToHtml("---\ntags: [a]\n---\n| a | b |\n|---|---|\n| 1 | 2 |\n\n$x^2$\n\n```mermaid\ngraph TD; A-->B\n```\n");

        Assert.DoesNotContain("tags:", html);
        Assert.Contains("<table", html);
        Assert.Contains("class=\"math\"", html);
        Assert.Contains("class=\"mermaid\"", html);
    }

    [Fact]
    public void WikiLinks()
    {
        var html = MarkdownRenderer.ToHtml("See [[Meeting notes]] and [[Projects/Plan|the plan]], not [normal](x.md).");

        Assert.Contains("<a href=\"wiki:Meeting%20notes\" class=\"wikilink\">Meeting notes</a>", html);
        Assert.Contains("<a href=\"wiki:Projects%2FPlan\" class=\"wikilink\">the plan</a>", html);
        Assert.Contains("<a href=\"x.md\">normal</a>", html);
    }

    [Fact]
    public void HeadingLinesBySlug()
    {
        const string text = "# Intro\n\n```\n# not a heading\n```\n## Next Steps!\n## Next Steps!\n";

        Assert.Equal(1, MarkdownHeadings.FindLine(text, "#intro"));
        Assert.Equal(6, MarkdownHeadings.FindLine(text, "next-steps"));
        Assert.Equal(7, MarkdownHeadings.FindLine(text, "next-steps-1"));
        Assert.Null(MarkdownHeadings.FindLine(text, "not-a-heading"));
    }

    [Fact]
    public void ResolvesWikiTargetsPreferringNeighbours()
    {
        string[] pages =
        [
            @"C:\nb\Inbox.md",
            @"C:\nb\Work\Plan.md",
            @"C:\nb\Home\Plan.md",
            @"C:\nb\Work\Deep\Plan.md",
        ];

        Assert.Equal(@"C:\nb\Home\Plan.md", WikiLinkResolver.Resolve("plan", @"C:\nb\Home\Other.md", pages));
        Assert.Equal(@"C:\nb\Work\Plan.md", WikiLinkResolver.Resolve("Plan", @"C:\nb\Inbox.md", pages));
        Assert.Equal(@"C:\nb\Work\Deep\Plan.md", WikiLinkResolver.Resolve("Deep/Plan", null, pages));
        Assert.Null(WikiLinkResolver.Resolve("Missing", null, pages));
    }
}
