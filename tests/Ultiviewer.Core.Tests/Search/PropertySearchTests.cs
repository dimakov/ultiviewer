using Ultiviewer.Core.Search;

namespace Ultiviewer.Core.Tests.Search;

public class PropertySearchTests
{
    private const string Project = """
        ---
        title: "Launch plan"
        status: Active
        priority: 2
        tags: [work, launch]
        owners:
          - Dana
          - 'Lee'
        meta:
          nested: skipped
        ---
        # Launch

        due:: 2026-11-01
        - [ ] Book venue [cost:: 1200]
        Talk about std::vector later.
        ```
        ignored:: yes
        ```
        """;

    [Fact]
    public void ExtractsFrontMatterAndInlineFields()
    {
        var properties = PageFacts.Extract(Project).Properties.Select(p => $"{p.Key}={p.Value}@{p.Line}").ToList();

        Assert.Equal(
            ["title=Launch plan@2", "status=Active@3", "priority=2@4", "tags=work@5", "tags=launch@5",
             "owners=Dana@7", "owners=Lee@8", "due=2026-11-01@14", "cost=1200@15"],
            properties);
    }

    [Fact]
    public void ParsesPropertyQueries()
    {
        var items = SearchQuery.Parse("status:done priority:>=2 due:<2026-11-01 project:* client:\"Acme  Corp\" -status:draft").Items;

        Assert.Equal(
            [
                new PropertyNode("status", PropertyOp.Equals, "done"),
                new PropertyNode("priority", PropertyOp.GreaterOrEqual, "2"),
                new PropertyNode("due", PropertyOp.Less, "2026-11-01"),
                new PropertyNode("project", PropertyOp.Exists, "*"),
                new PropertyNode("client", PropertyOp.Equals, "Acme Corp"),
                new NotNode(new PropertyNode("status", PropertyOp.Equals, "draft")),
            ],
            items);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("C:\\notes")]
    [InlineData("TODO:")]
    [InlineData("10:30")]
    [InlineData("is:something")]
    public void LeavesOtherColonWordsAsText(string word)
    {
        Assert.IsType<TermNode>(Assert.Single(SearchQuery.Parse(word).Items));
    }

    [Fact]
    public void QuotedPropertySyntaxStaysAPhrase()
    {
        Assert.Equal(new TermNode("status:done", true), Assert.Single(SearchQuery.Parse("\"status:done\"").Items));
    }

    [Theory]
    [InlineData("status:active", true)]
    [InlineData("status:ACTÍVE", true)]
    [InlineData("status:done", false)]
    [InlineData("-status:done", true)]
    [InlineData("priority:>=2", true)]
    [InlineData("priority:>10", false)]       // numeric, not text ("2" > "10" as text)
    [InlineData("due:<2026-12-01", true)]
    [InlineData("due:<2026-10-01", false)]
    [InlineData("owners:lee", true)]          // any value of a list
    [InlineData("tags:launch", true)]
    [InlineData("title:launch*", true)]
    [InlineData("title:*plan", true)]
    [InlineData("title:plan*", false)]
    [InlineData("cost:1200.0", true)]
    [InlineData("project:*", false)]
    [InlineData("ignored:*", false)]          // inside a code block
    [InlineData("std:*", false)]
    public void MatchesProperties(string query, bool expected)
    {
        Assert.Equal(expected, new TextMatcher(SearchQuery.Parse(query)).Matches(new PageContent("Launch", Project)));
    }

    [Fact]
    public void HighlightsTheMatchingPropertyLines()
    {
        var page = new PageContent("Launch", Project);
        var lines = new TextMatcher(SearchQuery.Parse("owners:* status:active")).FindMatches(page).Select(m => m.Line);

        Assert.Equal([3, 7, 8], lines);
    }

    [Fact]
    public void SortsNumbersNumericallyAndEmptyLast()
    {
        string?[] values = ["10", "", "9", "apple", null, "2026-01-05", "Banana"];
        var sorted = values.Order(Comparer<string?>.Create(PropertyValues.CompareForSort)).ToList();

        Assert.Equal(["9", "10", "2026-01-05", "apple", "Banana", "", null], sorted);
    }

    [Fact]
    public async Task IndexFindsTheSamePagesAsTheMatcher()
    {
        using var dir = new TempDirectory();
        var nb = dir.Folder("nb");
        dir.File("nb/Launch.md", Project);
        dir.File("nb/Old.md", "---\nstatus: done\npriority: 10\n---\nwrapped up");
        dir.File("nb/Idea.md", "Just text, status: active in prose.\nrating:: 4.5");

        using var index = NotebookIndex.Open(nb, dir.Combine("index"));
        index.Update();

        string Names(string q) =>
            string.Join(",", index.Candidates(SearchQuery.Parse(q)).Select(Path.GetFileNameWithoutExtension).Order());

        Assert.Equal("Launch", Names("status:active"));
        Assert.Equal("Old", Names("status:DONE"));
        Assert.Equal("Old", Names("priority:>2"));
        Assert.Equal("Launch,Old", Names("priority:*"));
        Assert.Equal("Idea", Names("rating:>=4"));
        Assert.Equal("Launch", Names("title:*plan"));
        Assert.Equal("Idea,Launch", Names("-status:done"));
        Assert.Equal("Launch", Names("due:>2026-10-31 owners:dana"));

        using var service = new SearchService(dir.Combine("index"));
        service.SetNotebooks([nb]);
        var results = new List<PageResult>();
        await foreach (var result in service.SearchAsync("priority:*", SearchScope.Everywhere, new Dictionary<string, string>()))
        {
            results.Add(result);
        }

        var old = Assert.Single(results, r => r.Title == "Old");
        Assert.Equal(["status=done", "priority=10"], old.Properties.Select(p => $"{p.Key}={p.Value}"));
        Assert.NotEqual(DateTime.MinValue, old.Modified);
        Assert.Equal([3], old.Hits.Select(h => h.Line));
    }
}
