using Ultiviewer.Core.Markdown;

namespace Ultiviewer.Core.Tests.Markdown;

public class MarkdownFormattingTests
{
    private static string Apply(string text, TextEdit edit) =>
        text[..edit.Start] + edit.Replacement + text[(edit.Start + edit.Length)..];

    [Theory]
    [InlineData("make it bold", 8, 4, InlineStyle.Bold, "make it **bold**")]
    [InlineData("make it **bold**", 10, 4, InlineStyle.Bold, "make it bold")]
    [InlineData("make it **bold**", 8, 8, InlineStyle.Bold, "make it bold")]
    [InlineData("word here", 2, 0, InlineStyle.Italic, "*word* here")]
    [InlineData("an *italic* one", 4, 6, InlineStyle.Italic, "an italic one")]
    [InlineData("a **bold** one", 4, 4, InlineStyle.Italic, "a ***bold*** one")]
    [InlineData("a ***both*** one", 5, 4, InlineStyle.Italic, "a **both** one")]
    [InlineData("a ***both*** one", 5, 4, InlineStyle.Bold, "a *both* one")]
    [InlineData("under", 0, 5, InlineStyle.Underline, "<u>under</u>")]
    [InlineData("<u>under</u>", 3, 5, InlineStyle.Underline, "under")]
    [InlineData("gone", 0, 4, InlineStyle.Strikethrough, "~~gone~~")]
    [InlineData("H2O", 1, 1, InlineStyle.Subscript, "H~2~O")]
    [InlineData("mark", 0, 4, InlineStyle.Highlight, "==mark==")]
    [InlineData("שלום עולם", 1, 0, InlineStyle.Bold, "**שלום** עולם")]
    [InlineData("a  b", 2, 0, InlineStyle.Bold, "a **** b")]
    [InlineData("<u>under</u> next", 10, 0, InlineStyle.Strikethrough, "<u>under</u>~~~~ next")]
    [InlineData("<u>under</u> next", 8, 0, InlineStyle.Bold, "<u>**under**</u> next")]
    [InlineData("1 < b > 2", 5, 0, InlineStyle.Bold, "1 < **b** > 2")]
    public void TogglesInlineStyles(string text, int start, int length, InlineStyle style, string expected) =>
        Assert.Equal(expected, Apply(text, MarkdownFormatting.ToggleInline(text, start, length, style)));

    [Fact]
    public void SelectionFollowsTheText()
    {
        var edit = MarkdownFormatting.ToggleInline("make it bold", 8, 4, InlineStyle.Bold);
        Assert.Equal((10, 4), (edit.SelectionStart, edit.SelectionLength));

        var empty = MarkdownFormatting.ToggleInline("a  b", 2, 0, InlineStyle.Bold);
        Assert.Equal((4, 0), (empty.SelectionStart, empty.SelectionLength));
    }

    [Theory]
    [InlineData("Title", 1, "# Title")]
    [InlineData("## Title", 3, "### Title")]
    [InlineData("### Title", 0, "Title")]
    public void Headings(string line, int level, string expected) =>
        Assert.Equal(expected, MarkdownFormatting.SetHeading(line, level));

    [Fact]
    public void Lists()
    {
        Assert.Equal(["- a", "", "  - b"], MarkdownFormatting.ToggleList(["a", "", "  b"], ListStyle.Bullet));
        Assert.Equal(["a", "b"], MarkdownFormatting.ToggleList(["- a", "* b"], ListStyle.Bullet));
        Assert.Equal(["1. a", "2. b"], MarkdownFormatting.ToggleList(["- a", "b"], ListStyle.Numbered));
        Assert.Equal(["> a", "> b"], MarkdownFormatting.ToggleList(["a", "b"], ListStyle.Quote));
        Assert.Equal(["a"], MarkdownFormatting.ToggleList(["> a"], ListStyle.Quote));
    }

    [Fact]
    public void ClearsCharacterFormattingOnly()
    {
        Assert.Equal(
            "bold italic under red snake_case_name mark code",
            MarkdownFormatting.ClearFormatting("**bold** *italic* <u>under</u> <span style=\"color:#c00\">red</span> snake_case_name ==mark== `code`"));
    }

    [Fact]
    public void ColorsAndLinks()
    {
        var colored = MarkdownFormatting.SetColor("red text", 0, 3, "#D64545");
        Assert.Equal("<span style=\"color:#D64545\">red</span> text", Apply("red text", colored));

        var recolor = "<span style=\"color:#D64545\">red</span>";
        Assert.Equal("red", Apply(recolor, MarkdownFormatting.SetColor(recolor, 0, recolor.Length, null)));

        var link = MarkdownFormatting.MakeLink("see docs", 4, 4, "https://example.com");
        Assert.Equal("see [docs](https://example.com)", Apply("see docs", link));
        Assert.Equal((11, 19), (link.SelectionStart, link.SelectionLength));
    }
}
