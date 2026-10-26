using Ultiviewer.Core.OneNote;

namespace Ultiviewer.Core.Tests.OneNote;

public class OneNoteMarkdownTests
{
    private static readonly OneNoteStyle Plain = OneNoteStyle.Plain;

    /// <summary>A rich text made of (text, formatting) pieces.</summary>
    private static OneNoteText Text(string? style, params (string Text, OneNoteStyle Style)[] pieces)
    {
        var runs = new List<OneNoteRun>();
        var position = 0;
        foreach (var (text, format) in pieces)
        {
            runs.Add(new OneNoteRun(position, position + text.Length, format));
            position += text.Length;
        }

        return new OneNoteText(string.Concat(pieces.Select(p => p.Text)), runs, style, []);
    }

    private static OneNoteText Text(string text, string? style = null) => Text(style, (text, Plain));

    private static OneNoteElement Para(OneNoteContent content, OneNoteList? list = null, params OneNoteElement[] children) =>
        new([content], children, list);

    private static ConvertedPage Convert(params OneNoteBlock[] blocks) =>
        OneNoteMarkdown.Convert(new OneNotePage("", 1, null, null, blocks), name => "Page.assets/" + Uri.EscapeDataString(name));

    private static string Md(params OneNoteElement[] elements) => Convert(new OneNoteOutline(elements)).Markdown.TrimEnd('\n');

    [Fact]
    public void WritesFrontMatterAndTitle()
    {
        var created = new DateTime(2024, 5, 1, 9, 30, 0, DateTimeKind.Local);
        var page = new OneNotePage("Plans", 1, created, created.AddDays(1), [new OneNoteOutline([Para(Text("Hello"))])]);

        var markdown = OneNoteMarkdown.Convert(page, n => n).Markdown;

        Assert.Equal("---\ncreated: 2024-05-01 09:30\nmodified: 2024-05-02 09:30\nsource: OneNote\n---\n\n# Plans\n\nHello\n", markdown);
    }

    [Fact]
    public void FormatsRunsAndMergesEqualNeighbours()
    {
        var bold = Plain with { Bold = true };
        var text = Text(null,
            ("Some ", Plain), ("bold", bold), (" text", bold), (", ", Plain), ("slanted ", Plain with { Italic = true }),
            ("under", Plain with { Underline = true }), (" ", Plain), ("gone", Plain with { Strikethrough = true }), (" x", Plain),
            ("2", Plain with { Superscript = true }), (" ", Plain), ("marked", Plain with { Highlight = 0xFFFF00 }), (" ", Plain),
            ("red", Plain with { Color = 0xFF0000 }), (" ", Plain), ("code()", Plain with { Font = "Consolas" }));

        Assert.Equal(
            "Some **bold text**, *slanted* <u>under</u> ~~gone~~ x<sup>2</sup> ==marked== <span style=\"color:#FF0000\">red</span> `code()`",
            Md(Para(text)));
    }

    [Fact]
    public void EscapesTextThatWouldBecomeMarkdown()
    {
        Assert.Equal("\\# not a heading, 2 \\* 3 \\[x\\] snake_case\\_ \\<b>", Md(Para(Text("# not a heading, 2 * 3 [x] snake_case_ <b>"))));
        Assert.Equal("1\\. first\\\n\\- second", Md(Para(Text("1. first\u000B- second"))));
    }

    [Fact]
    public void ResolvesHyperlinksAndOneNotePageLinks()
    {
        var link = Plain with { Hyperlink = true };
        var hidden = link with { Hidden = true };
        var text = Text(null,
            ("See ", Plain), ("﷟HYPERLINK \"https://example.com/a b\"", hidden), ("the site", link with { Underline = true }),
            (" and ", Plain), ("﷟HYPERLINK \"onenote:Notes.one#Project%20Plan&section-id={1}&page-id={2}&end\"", hidden),
            ("the plan", link), (" or ", Plain), ("﷟HYPERLINK \"onenote:Notes.one#C%23%20tips&end\"", hidden), ("C# tips", link));

        var converted = Convert(new OneNoteOutline([Para(text)]));

        Assert.Equal("See [the site](<https://example.com/a b>) and [[Project Plan|the plan]] or [[C tips|C# tips]]\n", converted.Markdown);
        Assert.Contains(converted.Notes, n => n.StartsWith("Links to OneNote pages", StringComparison.Ordinal));
    }

    [Fact]
    public void DropsLinkMarkersLeftInVisibleText()
    {
        // OneNote for the web can keep the marker in a run that isn't hidden; the link text follows in a link run.
        var text = Text(null,
            ("See \uFDDFHYPERLINK \"https://example.com/x\"", Plain), ("the docs", Plain with { Hyperlink = true }),
            (" and \uFDDFHYPERLINK \"https://example.com/y\"plain words", Plain));

        Assert.Equal("See [the docs](https://example.com/x) and plain words", Md(Para(text)));
    }

    [Fact]
    public void WritesHeadingsQuotesAndCodeBlocks()
    {
        var markdown = Md(
            Para(Text("Title", "h2")),
            Para(Text("Wise words", "quote")),
            Para(Text("var a = 1;", "code")),
            Para(Text("var b = 2;", "code")),
            Para(Text("After")));

        Assert.Equal("## Title\n\n> Wise words\n\n```\nvar a = 1;\nvar b = 2;\n```\n\nAfter", markdown);
    }

    [Fact]
    public void WritesNestedListsWithNumbering()
    {
        var bullet = new OneNoteList(false, null);
        var number = new OneNoteList(true, null);
        var markdown = Md(
            Para(Text("One"), number, Para(Text("Inner"), bullet), Para(Text("Inner 2"), bullet)),
            Para(Text("Two"), number),
            Para(Text("Between")),
            Para(Text("Restarted"), new OneNoteList(true, 5)),
            Para(Text("Next"), number));

        Assert.Equal("1. One\n   - Inner\n   - Inner 2\n2. Two\n\nBetween\n\n5. Restarted\n6. Next", markdown);
    }

    [Fact]
    public void FlattensChildrenOfPlainParagraphsKeepingListsApart()
    {
        // OneNote indents paragraphs under paragraphs; Markdown can't, so they follow at the same level.
        var bullet = new OneNoteList(false, null);
        var markdown = Md(
            Para(Text("Parent"), null, Para(Text("Item"), bullet), Para(Text("Child paragraph"))),
            Para(Text(""), null, Para(Text("Next list"), bullet)));

        Assert.Equal("Parent\n\n- Item\n\nChild paragraph\n\n- Next list", markdown);
    }

    [Fact]
    public void WritesToDosAndTags()
    {
        OneNoteText Tagged(string text, params OneNoteTag[] tags) => Text(text) with { Tags = tags };
        var markdown = Md(
            Para(Tagged("Buy milk", new OneNoteTag("To Do", 1, true, false))),
            Para(Tagged("Call Bob", new OneNoteTag("To Do", 1, true, true)), null, Para(Tagged("Sub task", new OneNoteTag("To Do", 1, true, false)))),
            Para(Tagged("An idea", new OneNoteTag("Remember for later", 3, false, false), new OneNoteTag("Important", 13, false, false))));

        Assert.Equal("- [ ] Buy milk\n- [x] Call Bob\n  - [ ] Sub task\n\nAn idea #remember-for-later #important", markdown);
    }

    [Fact]
    public void WritesTablesWithFirstRowAsHeader()
    {
        var table = new OneNoteTable([
            [[Para(Text("Name"))], [Para(Text("Notes"))]],
            [[Para(Text("a|b"))], [Para(Text("line 1")), Para(Text("line 2"))]],
            [[Para(Text("short row"))]],
        ]);

        Assert.Equal("| Name | Notes |\n| --- | --- |\n| a\\|b | line 1<br>line 2 |\n| short row |  |", Md(Para(table)));
    }

    [Fact]
    public void SavesPicturesAndFilesAsAssetsWithUniqueNames()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
        byte[] jpg = [0xFF, 0xD8, 0xFF, 4, 5];
        var converted = Convert(
            new OneNoteFreeContent(new OneNoteImage(png, null, "A chart", null, null, null)),
            new OneNoteFreeContent(new OneNoteImage(jpg, null, null, "photo.jpeg", "https://example.com", null)),
            new OneNoteFreeContent(new OneNoteImage(png, null, null, null, null, null)),
            new OneNoteFreeContent(new OneNoteFile("report final.pdf", [1, 2], null)),
            new OneNoteFreeContent(new OneNoteImage(null, null, null, null, null, "it is stored in the cloud")),
            new OneNoteFreeContent(new OneNoteImage([], ".jpeg", null, null, null, null)));

        Assert.Equal(
            "![A chart](Page.assets/image.png)\n\n[![](Page.assets/photo.jpg)](https://example.com)\n\n![](Page.assets/image%202.png)\n\n"
            + "[report final.pdf](Page.assets/report%20final.pdf)\n\n*(picture not available)*\n",
            converted.Markdown); // the empty picture adds no second placeholder right after the first
        Assert.Equal(["image.png", "photo.jpg", "image 2.png", "report final.pdf"], converted.Assets.Select(a => a.Name));
        Assert.Contains(converted.Notes, n => n.Contains("it is stored in the cloud"));
        Assert.Contains(converted.Notes, n => n.Contains("the file holds no data for it"));
    }

    [Fact]
    public void LeavesOnePlaceholderForUnsupportedContentAndCountsNotes()
    {
        var ink = new OneNoteFreeContent(new OneNoteUnsupported("a handwritten drawing (ink)"));
        var converted = Convert(ink, ink, ink, new OneNoteOutline([Para(Text("Text"))]), ink);

        Assert.Equal("*(a handwritten drawing (ink) not imported)*\n\nText\n\n*(a handwritten drawing (ink) not imported)*\n", converted.Markdown);
        Assert.Equal(["Content that can't be converted was left out: a handwritten drawing (ink) (×4)"], converted.Notes);
    }

    [Fact]
    public void WritesEquationsAsPlainTextAndDropsInlineInk()
    {
        var math = Plain with { Math = true, Italic = true };
        var text = Text(null, ("E = ", Plain), ("﷐𝑚𝑐﷑2", math), (" and ￼ drawn", Plain));

        var converted = Convert(new OneNoteOutline([Para(text)]));

        Assert.Equal("E = 𝑚𝑐2 and  drawn\n", converted.Markdown);
        Assert.Equal(["Equations were imported as plain text", "Handwriting inside text (ink) was left out"], converted.Notes.Order());
    }

    [Theory]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38 }, null, ".gif")]
    [InlineData(new byte[] { 0x42, 0x4D, 0, 0 }, null, ".bmp")]
    [InlineData(new byte[] { 0xD7, 0xCD, 0xC6, 0x9A }, null, ".wmf")]
    [InlineData(new byte[] { 0, 1, 2 }, ".PNG", ".png")]
    [InlineData(new byte[] { 0, 1, 2 }, null, ".bin")]
    public void SniffsPictureFormats(byte[] data, string? recorded, string expected) =>
        Assert.Equal(expected, OneNoteMarkdown.ImageExtension(data, recorded));
}
