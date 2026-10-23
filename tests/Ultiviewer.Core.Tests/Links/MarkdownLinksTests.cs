using Ultiviewer.Core.Links;

namespace Ultiviewer.Core.Tests.Links;

public class MarkdownLinksTests
{
    [Fact]
    public void FindsAllLinkKinds()
    {
        const string text = """
            See [the plan](Plan%20v2.md#goals) and ![img](<My Page.assets/a b.png> "title").
            [ref]: ../Other/Page.md
            <img src="pic.png"> and [[Wiki page|label]] and [web](https://example.com).
            """;

        var links = MarkdownLinks.Find(text);

        Assert.Equal(
            ["Plan v2.md", "My Page.assets/a b.png", "../Other/Page.md", "pic.png", "Wiki page", "https://example.com"],
            links.Select(l => l.Path));
        Assert.Equal(
            [LinkSyntax.Inline, LinkSyntax.Inline, LinkSyntax.Reference, LinkSyntax.Html, LinkSyntax.Wiki, LinkSyntax.Inline],
            links.Select(l => l.Syntax));
        Assert.True(links[^1].IsExternal);
    }

    [Fact]
    public void IgnoresLinksInCode()
    {
        const string text = """
            `[a](inline.md)` real [b](real.md)
            ```
            [c](fenced.md)
            ```
            """;

        Assert.Equal(["real.md"], MarkdownLinks.Find(text).Select(l => l.Path));
    }

    [Fact]
    public void RewriteKeepsFragmentsAndStyle()
    {
        const string text = "[a](Old%20Page.md#top) [b](<Old Page.md>) [[Old Page]] [c](keep.md)";

        var result = MarkdownLinks.Rewrite(text, (link, path) => path == "Old Page.md" || path == "Old Page" ? path.Replace("Old", "New") : null);

        Assert.Equal("[a](New%20Page.md#top) [b](<New Page.md>) [[New Page]] [c](keep.md)", result);
    }

    [Fact]
    public void DestinationWithParentheses()
    {
        var link = Assert.Single(MarkdownLinks.Find("[w](Foo_(bar).md)"));
        Assert.Equal("Foo_(bar).md", link.Path);
        Assert.Equal("Foo%28bar%29.md", MarkdownLinks.Encode("Foo(bar).md"));
    }
}
