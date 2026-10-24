using Ultiviewer.Core.Clipping;

namespace Ultiviewer.Core.Tests.Clipping;

public class HtmlToMarkdownTests
{
    private static string Md(string html, Uri? baseUri = null) => new HtmlToMarkdown(baseUri).Convert(html).Markdown;

    [Fact]
    public void ConvertsInlineFormattingAndResolvesLinks()
    {
        var markdown = Md(
            "<h2>Title</h2><p>Some <b>bold</b>, <i>italic</i>, <u>under</u>, <s>gone</s>, <code>x</code>, <sup>2</sup>, <mark>hi</mark> and <a href=\"/docs\">a link</a>.</p>",
            new Uri("https://example.com/page"));

        Assert.Equal("## Title\n\nSome **bold**, *italic*, <u>under</u>, ~~gone~~, `x`, <sup>2</sup>, ==hi== and [a link](https://example.com/docs).", markdown);
    }

    [Fact]
    public void KeepsWhitespaceOutsideDelimiters()
    {
        Assert.Equal("a **bold** word", Md("<p>a<b> bold </b>word</p>"));
    }

    [Fact]
    public void ConvertsNestedOrderedAndTaskLists()
    {
        var markdown = Md("""
            <ul>
              <li>One
                <ul><li>Nested</li></ul>
              </li>
              <li><input type="checkbox" checked> Done</li>
              <li><input type="checkbox"> Todo</li>
            </ul>
            <ol start="3"><li>Three</li><li>Four</li></ol>
            """);

        Assert.Equal("- One\n  - Nested\n- [x] Done\n- [ ] Todo\n\n3. Three\n4. Four", markdown);
    }

    [Fact]
    public void ConvertsTablesWithAlignmentAndEscapedPipes()
    {
        var markdown = Md("""
            <table>
              <tr><th>Name</th><th align="right">Price</th><th style="text-align:center">Note</th></tr>
              <tr><td>Tea</td><td>3</td><td>a | b</td></tr>
              <tr><td colspan="2">Total</td><td><p>x</p><p>y</p></td></tr>
            </table>
            """);

        Assert.Equal("| Name | Price | Note |\n| --- | ---: | :---: |\n| Tea | 3 | a \\| b |\n| Total |  | x<br>y |", markdown);
    }

    [Fact]
    public void TreatsLayoutTablesAsContent()
    {
        Assert.Equal("Only text", Md("<table><tr><td><p>Only text</p></td></tr></table>"));
    }

    [Fact]
    public void ConvertsCodeBlocksWithLanguageAndLinePerElement()
    {
        Assert.Equal("```python\nprint('hi')\n  x = 1\n```", Md("<pre><code class=\"language-python\">print('hi')\n  x = 1\n</code></pre>"));
        Assert.Equal("```\nconst a = 1;\nconst b = `x`;\n```", Md("<pre><div>const a = 1;</div><div>const b = `x`;</div></pre>"));
        Assert.Equal("Use ``a`b`` here", Md("<p>Use <code>a`b</code> here</p>"));
    }

    [Fact]
    public void ReadsGoogleDocsStyles()
    {
        var markdown = Md("""
            <b style="font-weight:normal;" id="docs-internal-guid-1">
              <p dir="ltr"><span style="font-weight:700">Bold</span><span style="font-weight:400"> and </span><span style="font-style:italic">italic</span><span style="text-decoration:line-through"> old</span></p>
              <ul><li dir="ltr" aria-level="1"><p dir="ltr"><span>One</span></p></li></ul>
            </b>
            """);

        Assert.Equal("**Bold** and *italic* ~~old~~\n\n- One", markdown);
    }

    [Fact]
    public void TurnsWordListParagraphsIntoLists()
    {
        var markdown = Md("""
            <p class=MsoNormal>Intro</p>
            <p class=MsoListParagraphCxSpFirst style='mso-list:l0 level1 lfo1'><![if !supportLists]><span style='font-family:Symbol'><span style='mso-list:Ignore'>·<span>&nbsp;&nbsp;</span></span></span><![endif]>First</p>
            <p class=MsoListParagraphCxSpMiddle style='mso-list:l0 level2 lfo1'><![if !supportLists]><span style='mso-list:Ignore'>o<span>&nbsp;</span></span><![endif]><b>Second</b></p>
            <p class=MsoListParagraphCxSpLast style='mso-list:l1 level1 lfo2'><![if !supportLists]><span style='mso-list:Ignore'>1.<span>&nbsp;</span></span><![endif]>Third</p>
            """);

        Assert.Equal("Intro\n\n- First\n    - **Second**\n1. Third", markdown);
    }

    [Fact]
    public void EscapesTextThatWouldBecomeMarkdown()
    {
        Assert.Equal("5 \\* 3, costs \\$5, file_name, \\_x\\_ and # mid\n\n1\\. Not a list\n\n\\# Not a heading",
            Md("<p>5 * 3, costs $5, file_name, _x_ and # mid</p><p>1. Not a list</p><p># Not a heading</p>"));
    }

    [Fact]
    public void LineBreaksBecomeHardBreaksAndDoubleBreaksParagraphs()
    {
        Assert.Equal("one\\\ntwo\n\nthree", Md("<p>one<br>two<br><br>three</p>"));
    }

    [Fact]
    public void ReportsWhetherThereWasFormatting()
    {
        Assert.False(new HtmlToMarkdown().Convert("<div><span style=\"color:#569cd6\">const</span> x = 1;</div><div>y</div>").HasFormatting);
        Assert.True(new HtmlToMarkdown().Convert("<p>a <em>b</em></p>").HasFormatting);
    }

    [Fact]
    public void DropsHiddenAndScriptedParts()
    {
        Assert.Equal("Visible", Md("<p>Visible</p><script>alert(1)</script><p style=\"display: none\">Hidden</p><div hidden>Also</div><button>Click</button>"));
    }

    [Fact]
    public void PrefersLazyImageSourcesAndUsesTheCallback()
    {
        var seen = new List<string>();
        var converter = new HtmlToMarkdown(new Uri("https://site.test/a/"), (src, alt) =>
        {
            seen.Add(src + "|" + alt);
            return src.EndsWith("skip.png", StringComparison.Ordinal) ? null : "local/" + Path.GetFileName(src);
        });

        var markdown = converter.Convert("""
            <p><img src="data:image/gif;base64,R0lGOD" data-src="pics/real.jpg" alt="Real [1]"></p>
            <p><img srcset="small.png 300w, big.png 1200w" alt=""></p>
            <p><img src="/skip.png"></p>
            <p><a href="https://x.test/"><img src="i.png" alt="logo"></a></p>
            """).Markdown;

        Assert.Equal("![Real \\[1\\]](local/real.jpg)\n\n![](local/big.png)\n\n[![logo](local/i.png)](https://x.test/)", markdown);
        Assert.Equal(["https://site.test/a/pics/real.jpg|Real [1]", "https://site.test/a/big.png|", "https://site.test/skip.png|", "https://site.test/a/i.png|logo"], seen);
    }

    [Fact]
    public void WritesAutolinksAndBracketsAwkwardUrls()
    {
        Assert.Equal("<https://a.test/x> and [doc](<https://a.test/my file (1).pdf>)",
            Md("<p><a href=\"https://a.test/x\">https://a.test/x</a> and <a href=\"https://a.test/my file (1).pdf\">doc</a></p>"));
    }

    [Fact]
    public void QuotesAndRules()
    {
        Assert.Equal("> Quoted\n>\n> - item\n\n---\n\nAfter", Md("<blockquote><p>Quoted</p><ul><li>item</li></ul></blockquote><hr><p>After</p>"));
    }
}

public class ClipboardHtmlTests
{
    [Fact]
    public void ExtractsTheFragmentAndSourceUrl()
    {
        const string body = "<html><body><!--StartFragment--><p>Héllo <b>you</b></p><!--EndFragment--></body></html>";
        var header = "Version:0.9\r\nStartHTML:0000000000\r\nEndHTML:0000000000\r\nStartFragment:0000000000\r\nEndFragment:0000000000\r\nSourceURL:https://example.com/post\r\n";

        var fragment = ClipboardHtml.Parse(header + body)!;

        Assert.Equal("<p>Héllo <b>you</b></p>", fragment.Html);
        Assert.Equal(new Uri("https://example.com/post"), fragment.SourceUrl);
    }

    [Fact]
    public void FallsBackToByteOffsets()
    {
        const string html = "<p>ש</p>";
        const string prefix = "Version:0.9\r\nStartFragment:0000000099\r\nEndFragment:0000000099\r\n";
        var start = System.Text.Encoding.UTF8.GetByteCount(prefix);
        var end = start + System.Text.Encoding.UTF8.GetByteCount(html);
        var data = prefix.Replace("StartFragment:0000000099", $"StartFragment:{start:D10}").Replace("EndFragment:0000000099", $"EndFragment:{end:D10}") + html;

        Assert.Equal(html, ClipboardHtml.Parse(data)!.Html);
        Assert.Null(ClipboardHtml.Parse(""));
    }
}
