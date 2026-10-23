using Ultiviewer.Core.Text;

namespace Ultiviewer.Core.Tests.Text;

public class TextOperationsTests
{
    [Theory]
    [InlineData("cat", false, false, false, "Cat cat concat", 3)]
    [InlineData("cat", true, false, false, "Cat cat concat", 2)]
    [InlineData("cat", false, true, false, "Cat cat concat", 2)]
    [InlineData(@"c\w+", false, false, true, "Cat cat concat", 3)]
    [InlineData("a.b", false, false, false, "a.b axb", 1)]
    [InlineData("^x", false, false, true, "x\nax\nx", 2)]
    public void FindsMatches(string pattern, bool matchCase, bool wholeWord, bool regex, string text, int expected)
    {
        var re = TextSearcher.Build(pattern, new FindOptions(matchCase, wholeWord, regex));
        Assert.Equal(expected, TextSearcher.FindAll(text, re).Count);
    }

    [Fact]
    public void FindWithinRangeAndSkipsEmptyMatches()
    {
        var re = TextSearcher.Build("x*", new FindOptions(UseRegex: true));
        Assert.Equal([new TextRange(1, 2), new TextRange(4, 1)], TextSearcher.FindAll("axxbx", re));

        var literal = TextSearcher.Build("a", new FindOptions());
        Assert.Equal([new TextRange(2, 1)], TextSearcher.FindAll("a a a", literal, start: 1, end: 4));
    }

    [Fact]
    public void ReplacesWithGroups()
    {
        var options = new FindOptions(UseRegex: true);
        var re = TextSearcher.Build(@"(\w+)@(\w+)", options);
        var text = "mail bob@home and amy@work";

        Assert.Equal(("mail home/bob and work/amy", 2), TextSearcher.ReplaceAll(text, re, "$2/$1", options));
        var first = TextSearcher.FindAll(text, re)[0];
        Assert.Equal("home/bob", TextSearcher.ReplacementFor(text, first, re, "$2/$1", options));
        Assert.Equal("$2", TextSearcher.ReplacementFor(text, first, re, "$2", new FindOptions()));
        Assert.Throws<System.Text.RegularExpressions.RegexParseException>(() => TextSearcher.Build("(", options));
    }

    [Fact]
    public void LineOperationsWork()
    {
        string[] lines = ["b 10", "a", "b 2", "a", "  ", "c  "];

        Assert.Equal(["  ", "a", "a", "b 2", "b 10", "c  "], LineOperations.SortAscending(lines));
        Assert.Equal(["b 10", "a", "b 2", "  ", "c  "], LineOperations.RemoveDuplicates(lines));
        Assert.Equal(["b 10", "a", "b 2", "a", "c  "], LineOperations.RemoveEmpty(lines));
        Assert.Equal("c", LineOperations.TrimTrailing(lines)[5]);
        Assert.Equal("one two three", LineOperations.Join(["one  ", "   two", "", "three"]));
        Assert.Equal("Hello World", LineOperations.ToTitleCase("hELLO wORLD"));
    }
}
