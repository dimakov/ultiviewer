using Ultiviewer.Core.Search;

namespace Ultiviewer.Core.Tests.Search;

public class CommandRankingTests
{
    private sealed record Item(string Title, string Location)
    {
        public string Key => Location + " › " + Title;
    }

    private static readonly Item[] Items =
    [
        new("Save", "File"),
        new("Save all", "File"),
        new("Duplicate line", "Edit › Line operations"),
        new("Delete line", "Edit › Line operations"),
        new("Word wrap", "View"),
    ];

    private static IReadOnlyList<string> Titles(string query, params string[] recent) =>
        CommandRanking.Rank(Items, query, recent, i => i.Key, i => i.Title, i => i.Location).Select(i => i.Title).ToList();

    [Fact]
    public void EmptyQueryPutsRecentCommandsFirstThenMenuOrder()
    {
        Assert.Equal(["Word wrap", "Delete line", "Save", "Save all", "Duplicate line"],
            Titles("", "View › Word wrap", "Edit › Line operations › Delete line"));
    }

    [Fact]
    public void QueryMatchesTitlesFuzzily()
    {
        Assert.Equal(["Save", "Save all"], Titles("save"));
        Assert.Equal(["Word wrap"], Titles("wwr"));
    }

    [Fact]
    public void MenuPathMatchesWhenTheTitleAloneDoesNot()
    {
        Assert.Equal(["Duplicate line"], Titles("line dup"));
    }

    [Fact]
    public void RecentUseBreaksTies()
    {
        Assert.Equal(["Delete line", "Duplicate line"], Titles("line", "Edit › Line operations › Delete line").Take(2));
    }

    [Fact]
    public void RememberMovesToFrontAndCaps()
    {
        var recent = Enumerable.Range(0, CommandRanking.MaxRecent).Select(i => "c" + i).ToList();
        CommandRanking.Remember(recent, "c5");
        Assert.Equal("c5", recent[0]);
        Assert.Equal(CommandRanking.MaxRecent, recent.Count);
        CommandRanking.Remember(recent, "new");
        Assert.Equal(["new", "c5", "c0"], recent.Take(3));
        Assert.Equal(CommandRanking.MaxRecent, recent.Count);
    }
}
