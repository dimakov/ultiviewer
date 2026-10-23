using Ultiviewer.Core.Search;

namespace Ultiviewer.Core.Tests.Search;

public class SearchQueryAndMatcherTests
{
    [Fact]
    public void ParsesAllSyntax()
    {
        var query = SearchQuery.Parse("kitchen \"cut list\" oak OR maple -pine #home/reno is:todo");

        Assert.Equal(
            [
                new TermNode("kitchen", false),
                new TermNode("cut list", true),
                new OrNode([new TermNode("oak", false), new TermNode("maple", false)]),
                new NotNode(new TermNode("pine", false)),
                new TagNode("home/reno"),
                new TaskNode(false),
            ],
            query.Items, new NodeComparer());
    }

    [Fact]
    public void FoldsCaseAndAccentsAndMapsBack()
    {
        var folded = TextFolding.FoldWithMap("Café ÜBER נִקּוּד");

        Assert.Equal("cafe uber נקוד", folded.Text);
        Assert.Equal(3, folded.Map[3]); // "e" came from "é"
    }

    [Theory]
    [InlineData("מטבח", "תכנון במטבח החדש", true)]   // Hebrew prefix letter: substring match
    [InlineData("cafe", "Visited the CAFÉ", true)]
    [InlineData("ar", "a car park", false)]          // short terms only match word starts
    [InlineData("ca", "a car park", true)]
    [InlineData("\"cut list\"", "the cut list here", true)]
    [InlineData("\"cut list\"", "cut the list", false)]
    [InlineData("oak OR maple", "maple boards", true)]
    [InlineData("oak -pine", "oak and pine", false)]
    [InlineData("oak -pine", "oak only", true)]
    public void MatchesPages(string query, string text, bool expected)
    {
        var matcher = new TextMatcher(SearchQuery.Parse(query));
        Assert.Equal(expected, matcher.Matches(new PageContent("Page", text)));
    }

    [Fact]
    public void AllTermsMustAppearSomewhereOnThePageIncludingTitle()
    {
        var matcher = new TextMatcher(SearchQuery.Parse("kitchen budget"));

        Assert.True(matcher.Matches(new PageContent("Kitchen", "first line\nthe budget is fine")));
        Assert.False(matcher.Matches(new PageContent("Bathroom", "the budget is fine")));
    }

    [Fact]
    public void HighlightsInOriginalCoordinates()
    {
        var matcher = new TextMatcher(SearchQuery.Parse("cafe ber"));
        var matches = matcher.FindMatches(new PageContent("t", "intro\nThe Café über"));

        Assert.Equal([new LineMatch(2, 4, 4), new LineMatch(2, 10, 3)], matches);
    }

    [Fact]
    public void TagsAndTasks()
    {
        const string text = "---\ntags: [Work, home/reno]\n---\nText #idea and #Idea/Big\n```\n#not-a-tag\n```\n- [ ] open one\n- [x] closed\n#123 isn't a tag";
        var facts = PageFacts.Extract(text);

        Assert.Equal(["work", "home/reno", "idea", "idea/big"], facts.Tags.Select(t => t.Tag));
        Assert.Equal([new TaskItem(8, false, "open one"), new TaskItem(9, true, "closed")], facts.Tasks);

        var page = new PageContent("p", text);
        Assert.True(new TextMatcher(SearchQuery.Parse("#idea")).Matches(page));
        Assert.True(new TextMatcher(SearchQuery.Parse("#home")).Matches(page));
        Assert.False(new TextMatcher(SearchQuery.Parse("#hom")).Matches(page));
        Assert.True(new TextMatcher(SearchQuery.Parse("is:todo")).Matches(page));
        Assert.Equal([8], new TextMatcher(SearchQuery.Parse("is:todo")).FindMatches(page).Select(m => m.Line));
    }

    [Fact]
    public void SplitsBlocks()
    {
        const string text = "# Title\npara one\nstill para\n\n- item a\n- item b\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n```\ncode\n\nmore code\n```\nend";
        var blocks = MarkdownBlocks.Split(text);

        Assert.Equal([1, 2, 5, 6, 8, 12, 17], blocks.Select(b => b.Line));
        Assert.Contains("more code", blocks[5].Text);
    }

    [Fact]
    public void FuzzyPrefersPrefixAndWordStarts()
    {
        var meeting = FuzzyMatcher.Score("mn", "Meeting notes");
        var random = FuzzyMatcher.Score("mn", "Summary of nothing");

        Assert.NotNull(meeting);
        Assert.True(meeting > random);
        Assert.Null(FuzzyMatcher.Score("xyz", "Meeting notes"));
        Assert.True(FuzzyMatcher.Score("meet", "Meeting notes") > FuzzyMatcher.Score("meet", "Notes from the meeting"));
    }

    private sealed class NodeComparer : IEqualityComparer<QueryNode>
    {
        public bool Equals(QueryNode? x, QueryNode? y) => (x, y) switch
        {
            (OrNode a, OrNode b) => a.Items.SequenceEqual(b.Items, this),
            (AndNode a, AndNode b) => a.Items.SequenceEqual(b.Items, this),
            (NotNode a, NotNode b) => Equals(a.Inner, b.Inner),
            _ => Equals((object?)x, y),
        };

        public int GetHashCode(QueryNode obj) => 0;
    }
}

public class NotebookIndexTests
{
    [Fact]
    public async Task IndexesQueriesAndUpdatesIncrementally()
    {
        using var dir = new TempDirectory();
        var nb = dir.Folder("nb");
        dir.File("nb/Kitchen.md", "# Kitchen\nתכנון במטבח\n- [ ] buy oak #reno");
        dir.File("nb/Work/Plan.md", "Budget for Q3\nMeeting at the café");
        dir.File("nb/Work/Plan.assets/notes.md", "hidden attachment mentioning budget");
        dir.File("nb/.hidden.md", "budget");

        using var index = NotebookIndex.Open(nb, dir.Combine("index"));
        Assert.Equal((2, 0), index.Update());
        Assert.Equal((0, 0), index.Update());

        string Names(string q, string? scope = null) =>
            string.Join(",", index.Candidates(SearchQuery.Parse(q), scope).Select(Path.GetFileNameWithoutExtension).Order());

        Assert.Equal("Plan", Names("budget"));
        Assert.Equal("Kitchen", Names("מטבח"));
        Assert.Equal("Plan", Names("CAFE"));
        Assert.Equal("Kitchen", Names("#reno"));
        Assert.Equal("Kitchen", Names("is:todo"));
        Assert.Equal("Kitchen", Names("kit"));            // title match
        Assert.Equal("Plan", Names("q3"));                // short term → word prefix
        Assert.Equal("Kitchen,Plan", Names("kitchen OR budget"));
        Assert.Equal("Kitchen", Names("-budget oak"));
        Assert.Equal("", Names("budget", dir.Combine("nb/Other")));
        Assert.Equal("Plan", Names("budget", dir.Combine("nb/Work")));

        File.WriteAllText(dir.Combine("nb/Work/Plan.md"), "nothing here now");
        File.SetLastWriteTimeUtc(dir.Combine("nb/Work/Plan.md"), DateTime.UtcNow.AddMinutes(1));
        File.Delete(dir.Combine("nb/Kitchen.md"));
        Assert.Equal((1, 1), index.Update());
        Assert.Equal("", Names("budget"));
        Assert.Empty(index.Tags());

        // Reopening keeps the data; the search service merges unsaved buffers.
        using var service = new SearchService(dir.Combine("index"));
        service.SetNotebooks([nb]);
        var buffers = new Dictionary<string, string> { [dir.Combine("nb/Work/Plan.md")] = "unsaved budget line" };
        var results = new List<PageResult>();
        await foreach (var result in service.SearchAsync("budget", SearchScope.Everywhere, buffers))
        {
            results.Add(result);
        }

        var hit = Assert.Single(Assert.Single(results).Hits);
        Assert.Equal(new SearchHit(1, 8, 6, "unsaved budget line"), hit);
    }

    [Fact]
    public void RebuildsCorruptIndex()
    {
        using var dir = new TempDirectory();
        var nb = dir.Folder("nb");
        dir.File("nb/a.md", "alpha");
        var folder = dir.Combine("index");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, NotebookIndex.IdFor(nb) + ".db"), "this is not a database");

        using var index = NotebookIndex.Open(nb, folder);
        index.Update();

        Assert.Single(index.Candidates(SearchQuery.Parse("alpha")));
    }
}
