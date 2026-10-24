using Ultiviewer.Core.Links;

namespace Ultiviewer.Core.Tests.Links;

public class PageLinksTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "uv-links"));
    private static readonly string Plan = Path.Combine(Root, "Work", "Plan.md");
    private static readonly string Budget = Path.Combine(Root, "Work", "Budget.md");
    private static readonly string[] Pages = [Plan, Budget, Path.Combine(Root, "Home.md")];

    [Fact]
    public void ResolvesOutgoingLinksAndMarksBrokenOnes()
    {
        const string text = "See [[Budget]] and [[Missing page]].\n[home](../Home.md) [web](https://example.com) [[#Local heading]]";

        var links = PageLinks.Outgoing(text, Plan, Pages, Root);

        Assert.Equal(
            [("Budget", LinkState.Ok, 1), ("Missing page", LinkState.Broken, 1), ("../Home.md", LinkState.Broken, 2), ("https://example.com", LinkState.External, 2)],
            links.Select(l => (l.Target, l.State, l.Line)));
        Assert.Equal(Budget, links[0].ResolvedPath);
    }

    [Fact]
    public void FindsLinesLinkingToAPage()
    {
        const string text = "intro\nfirst [[Budget]]\nthen [the numbers](Budget.md#totals)\n`[[Budget]]` in code\n[[Budget|money]] again";

        Assert.Equal([2, 3, 5], PageLinks.LinksTo(text, Plan, Budget, Pages, Root).Select(l => l.Line));
        Assert.Equal("first [[Budget]]", PageLinks.LinksTo(text, Plan, Budget, Pages, Root)[0].Text);
    }

    [Fact]
    public void FindsAndLinksUnlinkedMentions()
    {
        const string text = "The budget is due.\n[[Budget]] already linked\n[[Other|budget]] label\nbudgetary is another word\n`budget` in code";

        var mentions = PageLinks.Mentions(text, "Budget");

        Assert.Equal([1], mentions.Select(m => m.Line));
        Assert.Equal("The [[Budget|budget]] is due.\n[[Budget]] already linked", string.Join('\n', PageLinks.LinkMention(text, 1, "Budget").Split('\n')[..2]));
        Assert.Empty(PageLinks.Mentions("Up", "Up")); // titles shorter than 3 characters are too noisy
    }
}
