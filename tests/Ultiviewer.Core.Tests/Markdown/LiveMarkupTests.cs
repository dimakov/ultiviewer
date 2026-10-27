using System.Text;
using Ultiviewer.Core.Markdown;

namespace Ultiviewer.Core.Tests.Markdown;

public class LiveMarkupTests
{
    /// <summary>
    /// What Live mode shows with the caret at <paramref name="caret"/>: hidden markers removed, replacements as
    /// symbols (• bullet, ☐/☑ task, ▌ quote, — rule, [img:url], [table]), revealed spans as source.
    /// </summary>
    private static string Render(string text, int caret = -1)
    {
        var markup = LiveMarkup.Analyze(text);
        var output = new StringBuilder();
        var position = 0;
        for (var line = 1; line <= markup.LineCount; line++)
        {
            foreach (var span in markup.SpansOnLine(line))
            {
                if (!span.IsReplacement || span.Start < position || (caret >= 0 && span.IsRevealedBy(caret, caret)))
                {
                    continue;
                }

                output.Append(text, position, span.Start - position);
                output.Append(span.Style switch
                {
                    LiveStyle.Bullet => "•",
                    LiveStyle.TaskBox => span.Value == 1 ? "☑" : "☐",
                    LiveStyle.QuoteMarker => "▌",
                    LiveStyle.Rule => "—",
                    LiveStyle.Image => $"[img:{span.Text}|{span.Detail}]",
                    LiveStyle.Table => "[table]",
                    _ => "",
                });
                position = span.End;
            }
        }

        output.Append(text, position, text.Length - position);
        return output.ToString();
    }

    private static List<(string Text, LiveStyle Style)> Styles(string text) =>
        Enumerable.Range(1, LiveMarkup.Analyze(text).LineCount)
            .SelectMany(LiveMarkup.Analyze(text).SpansOnLine)
            .Where(s => !s.IsReplacement)
            .Select(s => (text.Substring(s.Start, s.Length), s.Style))
            .ToList();

    [Theory]
    [InlineData("# Title", "Title")]
    [InlineData("### Deep ###", "Deep")]
    [InlineData("make **bold** and *it* and ~~gone~~", "make bold and it and gone")]
    [InlineData("a ***both*** b", "a both b")]
    [InlineData("==mark== H~2~O x^2^ ++ins++", "mark H2O x2 ins")]
    [InlineData("use `code` here", "use code here")]
    [InlineData("see [the docs](https://example.com \"Title\") now", "see the docs now")]
    [InlineData("go to [[Some Page]] or [[Other|label]]", "go to Some Page or label")]
    [InlineData("<https://example.com>", "https://example.com")]
    [InlineData("<u>under</u> and <span style=\"color:#D64545\">red</span>", "under and red")]
    [InlineData("not \\*emphasis\\*", "not *emphasis*")]
    [InlineData("unknown <kbd>Ctrl</kbd> <abbr>x</abbr>", "unknown Ctrl <abbr>x</abbr>")]
    public void HidesInlineSyntax(string text, string expected) => Assert.Equal(expected, Render(text));

    [Fact]
    public void RevealsOnlyTheElementUnderTheCaret()
    {
        const string text = "make **bold** and *it*";

        Assert.Equal("make **bold** and it", Render(text, text.IndexOf("bold", StringComparison.Ordinal)));
        Assert.Equal("make **bold** and it", Render(text, text.IndexOf("**", StringComparison.Ordinal))); // touching the edge
        Assert.Equal("make bold and *it*", Render(text, text.Length));
    }

    [Fact]
    public void HeadingsRevealPerLine()
    {
        const string text = "# One\ntext\n## Two";

        Assert.Equal("# One\ntext\nTwo", Render(text, 2));
        Assert.Equal("One\ntext\nTwo", Render(text, 7));
        Assert.Equal("One\ntext\n## Two", Render(text, text.Length));
    }

    [Fact]
    public void ListsTasksAndQuotes()
    {
        const string text = "- one\n\t- two\n- [ ] todo\n- [x] done\n1. first\n> quoted\n> > nested";

        Assert.Equal("• one\n\t• two\n☐ todo\n☑ done\n1. first\n▌quoted\n▌▌nested", Render(text));

        // The caret after "- " (where typing happens) keeps the bullet; on the bullet it shows "-".
        Assert.Equal("• one", Render(text, 2)[..5]);
        Assert.Equal("- one", Render(text, 1)[..5]);
    }

    [Fact]
    public void CheckedTasksAreMarkedDone()
    {
        var styles = Styles("- [x] done\n- [ ] open");

        Assert.Contains(("done", LiveStyle.TaskDone), styles);
        Assert.Contains(("📅 2026-10-25", LiveStyle.TaskMeta), Styles("- [ ] pay 📅 2026-10-25 ⏫"));
        Assert.Contains(("⏫", LiveStyle.TaskMeta), Styles("- [ ] pay 📅 2026-10-25 ⏫"));
        Assert.DoesNotContain(styles, s => s is { Style: LiveStyle.TaskDone, Text: "open" });
    }

    [Fact]
    public void FencedCodeHidesFencesUntilTheCaretIsInside()
    {
        const string text = "before\n```csharp\nvar x = **1**;\n```\nafter";

        Assert.Equal("before\ncsharp\nvar x = **1**;\n\nafter", Render(text));
        Assert.Equal(text, Render(text, text.IndexOf("var", StringComparison.Ordinal)));

        var code = LiveMarkup.Analyze(text);
        Assert.Equal([1, 0, 2], Enumerable.Range(2, 3).Select(l => code.SpansOnLine(l).First(s => s.Style == LiveStyle.CodeBlock).Value));
        Assert.All(Enumerable.Range(2, 3), l => Assert.Equal("csharp", code.SpansOnLine(l)[0].Text));
    }

    [Fact]
    public void RulesImagesAndTables()
    {
        const string text = "above\n\n---\n\n![a cat|200](img/cat.png)\n\n| A | B |\n|---|:-:|\n| 1 | **2** |\n\nafter";

        Assert.Equal("above\n\n—\n\n[img:img/cat.png|a cat|200]\n\n[table]\n\nafter", Render(text));

        // Inside the table every cell is source, so the columns stay aligned.
        var inTable = Render(text, text.IndexOf("| 1", StringComparison.Ordinal));
        Assert.Contains("| 1 | **2** |", inTable, StringComparison.Ordinal);
    }

    [Fact]
    public void SetextHeadingsAndFrontMatter()
    {
        const string text = "---\ntags: [a]\n---\nTitle\n=====\nbody";

        Assert.Equal("---\ntags: [a]\n---\nTitle\n\nbody", Render(text));
        var styles = Styles(text);
        Assert.Contains(("tags: [a]", LiveStyle.FrontMatter), styles);
        Assert.Contains(("Title", LiveStyle.Heading), styles);
    }

    [Fact]
    public void StylesCoverTheVisibleText()
    {
        var styles = Styles("# Head\n**b** *i* `c` [l](u) #tag <span style=\"color: red\">r</span>");

        Assert.Contains(("# Head", LiveStyle.Heading), styles);
        Assert.Contains(("b", LiveStyle.Strong), styles);
        Assert.Contains(("i", LiveStyle.Emphasis), styles);
        Assert.Contains(("c", LiveStyle.Code), styles);
        Assert.Contains(("l", LiveStyle.Link), styles);
        Assert.Contains(("#tag", LiveStyle.Tag), styles);
        Assert.Contains(("r", LiveStyle.Color), styles);
    }

    [Fact]
    public void SpansAreSplitPerLineAndOffsetsSurviveCrLf()
    {
        const string text = "a **bold\r\ntext** b\r\n# H";
        var markup = LiveMarkup.Analyze(text);

        Assert.Equal(["bold"], markup.SpansOnLine(1).Where(s => s.Style == LiveStyle.Strong).Select(s => text.Substring(s.Start, s.Length)));
        Assert.Equal(["text"], markup.SpansOnLine(2).Where(s => s.Style == LiveStyle.Strong).Select(s => text.Substring(s.Start, s.Length)));
        Assert.Equal("# ", markup.SpansOnLine(3).Where(s => s.Style == LiveStyle.Marker).Select(s => text.Substring(s.Start, s.Length)).Single());
    }

    [Fact]
    public void RevealRegionCoversWholeBlocks()
    {
        const string text = "x\n```\na\nb\n```\ny";
        var markup = LiveMarkup.Analyze(text);
        var caret = text.IndexOf('b');

        Assert.Equal((2, text.IndexOf("\ny", StringComparison.Ordinal)), markup.RevealRegion(caret, caret));
        Assert.Equal((0, 1), markup.RevealRegion(0, 0));
    }

    private const string Sample = """
        ---
        tags: [demo]
        ---
        # Title

        Para with **bold** and [link](u) and [ref link][r].

        - item
        - item two

          continuation paragraph of item two

              indented code inside the item
        - [x] done

        ```
        code with a blank line

        inside
        ```

        <!-- a comment

        spanning a blank line -->

        $$
        x^2

        y
        $$

        | A | B |
        |---|---|
        | 1 | 2 |

        ---

        Setext
        ======

        [r]: https://example.com
        """;

    private static List<LiveSpan> Ordered(IEnumerable<LiveSpan> spans) =>
        [.. spans.OrderBy(s => s.Start).ThenBy(s => s.Length).ThenBy(s => s.Style)];

    [Fact]
    public void ChunkedAnalysisMatchesTheWholeDocument()
    {
        var text = Sample.Replace("[ref link][r]", "ref link", StringComparison.Ordinal); // references across chunks are the known gap

        Assert.Equal(Ordered(LiveMarkup.AnalyzeWhole(text)), Ordered(LiveMarkup.Analyze(text).AllSpans));
    }

    [Fact]
    public void ChunksSplitOnlyWhereABlockCannotContinue()
    {
        // The sample has CRLF line ends when git checks the file out that way on Windows.
        var chunks = LiveMarkup.Chunks(Sample).Select(c => Sample[c.Start..c.End].Split('\n')[0].TrimEnd('\r')).ToList();

        Assert.Equal(["---", "Para with **bold** and [link](u) and [ref link][r].", "- item", "```", "<!-- a comment", "$$", "| A | B |", "---", "Setext", "[r]: https://example.com"], chunks);
    }

    [Fact]
    public void ReanalysisReusesUnchangedChunksAndMatchesAFreshOne()
    {
        var before = LiveMarkup.Analyze(Sample);
        var edited = Sample.Replace("item two", "item **two**", StringComparison.Ordinal);

        Assert.Equal(Ordered(LiveMarkup.Analyze(edited).AllSpans), Ordered(LiveMarkup.Analyze(edited, before).AllSpans));
    }

    [Fact]
    public void AnalyzesLargeNotesQuickly()
    {
        // Live mode re-analyzes after every keystroke, so a long note must stay well under a frame or two.
        var section = "## Heading\n\nSome **bold** and *italic* text with a [link](https://example.com) and `code`.\n\n- [ ] task\n- item\n\n> quote\n\n```\ncode\n```\n\n";
        var text = string.Concat(Enumerable.Repeat(section, 1000)); // ~150 KB
        var previous = LiveMarkup.Analyze(text);
        var edited = text.Insert(text.Length / 2, "x");

        // Best of three, so a busy test machine doesn't fail it; a full analysis takes ~200 ms, an incremental one ~15 ms.
        var best = long.MaxValue;
        LiveMarkup? markup = null;
        for (var run = 0; run < 3; run++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            markup = LiveMarkup.Analyze(edited, previous);
            best = Math.Min(best, watch.ElapsedMilliseconds);
        }

        Assert.True(markup!.LineCount > 10000);
        Assert.True(best < 80, $"took {best} ms");
    }

    [Fact]
    public void ParsesTablesWithCellPositions()
    {
        const string text = "| Name | Qty |\n|:--|--:|\n| a \\| b | `x|y` |\nlast | row";
        var table = MarkdownTable.Parse(text, 0, text.Length)!;

        Assert.Equal(["Name", "Qty"], table.Header.Select(c => c.Text));
        Assert.Equal([TableAlignment.Left, TableAlignment.Right], table.Alignments);
        Assert.Equal(["a \\| b", "`x|y`"], table.Rows[0].Select(c => c.Text));
        Assert.Equal(["last", "row"], table.Rows[1].Select(c => c.Text));
        var cell = table.Rows[0][1];
        Assert.Equal("`x|y`", text.Substring(cell.Start, cell.Length));
        Assert.Null(MarkdownTable.Parse("| a |\n| b |", 0, 11));
    }
}
