using Ultiviewer.Core.Markdown;

namespace Ultiviewer.Core.Tests.Markdown;

/// <summary>Editing with hidden marks. "|" marks the caret in the inputs and outputs.</summary>
public class LiveMarkupEditingTests
{
    private static (string Text, int Caret) Parse(string marked) => (marked.Replace("|", ""), marked.IndexOf('|'));

    private static string Mark(string text, int caret) => text.Insert(caret, "|");

    /// <summary>Backspace as the editor does it: a whole removal, or a move and then one character back.</summary>
    private static string Backspace(string marked)
    {
        var (text, caret) = Parse(marked);
        var (target, remove) = LiveMarkup.Analyze(text).Backspace(caret);
        if (remove is { } range)
        {
            return Mark(text.Remove(range.Start, range.Length), range.Start);
        }

        return target == 0 ? Mark(text, 0) : Mark(text.Remove(target - 1, 1), target - 1);
    }

    private static string Delete(string marked)
    {
        var (text, caret) = Parse(marked);
        var (target, remove) = LiveMarkup.Analyze(text).Delete(caret);
        return remove is { } range ? Mark(text.Remove(range.Start, range.Length), range.Start) : Mark(text.Remove(target, 1), target);
    }

    private static string Enter(string marked)
    {
        var (text, caret) = Parse(marked);
        var (target, insert, at) = LiveMarkup.Analyze(text).Enter(caret);
        text = text.Insert(target, insert);
        var position = target + at;
        return Mark(text.Insert(position, "\n"), position + 1);
    }

    [Fact]
    public void InlineAndPrefixMarksStayHiddenButBlocksOpen()
    {
        const string text = "## Title\n\nSome **bold** text\n\n```\ncode\n```\n\n| a | b |\n|---|---|\n| 1 | 2 |\n";
        var markup = LiveMarkup.Analyze(text);
        bool Revealed(int line, LiveStyle style, int caret) =>
            markup.SpansOnLine(line).Where(s => s.Style == style).Any(s => markup.IsRevealedWithHiddenMarks(s, caret, caret));

        Assert.False(Revealed(1, LiveStyle.Marker, 4));                        // "## " with the caret on the heading
        Assert.False(Revealed(3, LiveStyle.Marker, text.IndexOf("bold")));     // "**" with the caret in the bold text
        Assert.False(Revealed(5, LiveStyle.Marker, text.IndexOf("code")));     // fence, caret inside the code
        Assert.True(Revealed(5, LiveStyle.Marker, text.IndexOf("```")));       // fence, caret on the fence line
        Assert.True(Revealed(9, LiveStyle.Table, text.IndexOf("| 1")));        // tables open for editing
    }

    [Theory]
    [InlineData("## Title", 3)]
    [InlineData("- [ ] task", 6)]
    [InlineData("> quote", 2)]
    [InlineData("  - item", 4)]
    [InlineData("**Bold** x", 2)]
    [InlineData("plain text", 0)]
    [InlineData("  indented", 0)]
    public void ProseStartsAfterTheHiddenPrefix(string line, int expected)
    {
        Assert.Equal(expected, LiveMarkup.Analyze(line).ProseStart(0));
    }

    [Theory]
    [InlineData("|## Title", "## |Title")]
    [InlineData("#|# Title", "## |Title")]
    [InlineData("|- item", "- |item")]
    [InlineData("a *|*b** c", "a **|b** c")]
    [InlineData("a **b*|* c", "a **b|** c")]
    [InlineData("a **b**| c", "a **b**| c")]
    public void CaretSnapsOutOfHiddenSyntax(string marked, string expected)
    {
        var (text, caret) = Parse(marked);
        Assert.Equal(expected, Mark(text, LiveMarkup.Analyze(text).SnapCaret(caret)));
    }

    [Theory]
    [InlineData("## |Title", "|Title")]
    [InlineData("> |quote", "|quote")]
    [InlineData("- [ ] |task", "|task")]
    [InlineData("a **bold**|", "a **bol|**")]
    [InlineData("a **bold|**", "a **bol|**")]
    [InlineData("a **b**|", "a |")]
    [InlineData("a **b|**", "a |")]
    [InlineData("x [link](http://a.test)|", "x [lin|](http://a.test)")]
    [InlineData("plain|", "plai|")]
    public void BackspaceDeletesVisibleTextAndPrefixes(string marked, string expected)
    {
        Assert.Equal(expected, Backspace(marked));
    }

    [Fact]
    public void BackspaceAtTheStartOfFormattedTextJoinsTheLines()
    {
        Assert.Equal("one|**Bold** x", Backspace("one\n**|Bold** x").Replace("\n", ""));
    }

    [Theory]
    [InlineData("a |**bold**", "a **|old**")]
    [InlineData("a **|b** c", "a | c")]
    [InlineData("a **bold|** c", "a **bold**|c")]
    public void DeleteSkipsHiddenMarks(string marked, string expected)
    {
        Assert.Equal(expected, Delete(marked));
    }

    [Theory]
    [InlineData("**bold|**", "**bold**\n|")]
    [InlineData("**|bold**", "\n|**bold**")]
    [InlineData("**bo|ld**", "**bo**\n|**ld**")]
    [InlineData("- *a **b|c** d*", "- *a **b***\n|***c** d*")]
    [InlineData("[link|](http://a.test) x", "[link](http://a.test)\n| x")]
    [InlineData("plain |text", "plain \n|text")]
    public void EnterKeepsFormattingIntact(string marked, string expected)
    {
        Assert.Equal(expected, Enter(marked));
    }
}
