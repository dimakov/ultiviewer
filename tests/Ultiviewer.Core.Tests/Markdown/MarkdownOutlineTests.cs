using Ultiviewer.Core.Markdown;

namespace Ultiviewer.Core.Tests.Markdown;

public class MarkdownOutlineTests
{
    private const string Page = """
        ---
        title: x
        ---
        # Title

        intro

        ## One

        text one

        ```
        # not a heading
        ```

        ## Two

        - a
          - a1
          - a2
        - b

        Setext
        ======
        """;

    private static string Apply(string text, TextEdit edit) => text[..edit.Start] + edit.Replacement + text[(edit.Start + edit.Length)..];

    [Fact]
    public void FindsHeadingsAndTheirSections()
    {
        var headings = MarkdownOutline.Headings(Page);

        Assert.Equal(
            [(1, "Title", 3, 21), (2, "One", 7, 14), (2, "Two", 15, 21), (1, "Setext", 22, 23)],
            headings.Select(h => (h.Level, h.Text, h.Line, h.EndLine)));
    }

    [Fact]
    public void FoldsSectionsCodeListsAndFrontMatter()
    {
        var folds = MarkdownOutline.Folds(Page);

        Assert.Contains(new FoldRange(0, 2, FoldKind.FrontMatter), folds);
        Assert.Contains(new FoldRange(3, 20, FoldKind.Section), folds); // trailing blank lines stay visible
        Assert.Contains(new FoldRange(7, 13, FoldKind.Section), folds);
        Assert.Contains(new FoldRange(11, 13, FoldKind.Code), folds);
        Assert.Contains(new FoldRange(17, 19, FoldKind.List), folds);
        Assert.DoesNotContain(folds, f => f.StartLine == 20); // "- b" has no children
    }

    [Fact]
    public void MovesHeadingSectionsPastTheirSiblings()
    {
        const string text = "# A\n\n## One\nfirst\n\n## Two\nsecond\n\n# B\n";
        var caret = text.IndexOf("## Two", StringComparison.Ordinal) + 3;

        var up = MarkdownOutline.MoveBlock(text, caret, up: true, "\n")!.Value;
        Assert.Equal("# A\n\n## Two\nsecond\n\n## One\nfirst\n\n# B\n", Apply(text, up));
        Assert.Equal("Two", Apply(text, up)[up.SelectionStart..][..3]);

        var down = MarkdownOutline.MoveBlock(text, text.IndexOf("## One", StringComparison.Ordinal), up: false, "\n")!.Value;
        Assert.Equal("# A\n\n## Two\nsecond\n\n## One\nfirst\n\n# B\n", Apply(text, down));

        Assert.Null(MarkdownOutline.MoveBlock(text, text.IndexOf("## One", StringComparison.Ordinal), up: true, "\n")); // first child
        Assert.Null(MarkdownOutline.MoveBlock(text, text.IndexOf("first", StringComparison.Ordinal), up: true, "\n")); // plain line
    }

    [Fact]
    public void MovesListItemsWithTheirChildren()
    {
        const string text = "- a\n  - a1\n- b\n  - b1\n- c";
        var caret = text.IndexOf("- b", StringComparison.Ordinal);

        Assert.Equal("- b\n  - b1\n- a\n  - a1\n- c", Apply(text, MarkdownOutline.MoveBlock(text, caret, up: true, "\n")!.Value));
        Assert.Equal("- a\n  - a1\n- c\n- b\n  - b1", Apply(text, MarkdownOutline.MoveBlock(text, caret, up: false, "\n")!.Value));
        Assert.Null(MarkdownOutline.MoveBlock(text, text.IndexOf("- a1", StringComparison.Ordinal), up: true, "\n")); // first child
    }

    [Fact]
    public void IndentsListItemsWithTheirChildren()
    {
        const string text = "- a\n- b\n  - b1\n- c";
        var caret = text.IndexOf("- b", StringComparison.Ordinal) + 2;

        var indent = MarkdownOutline.IndentListItem(text, caret, outdent: false, "\t")!.Value;
        Assert.Equal("- a\n\t- b\n\t  - b1\n- c", Apply(text, indent));
        Assert.Equal(caret + 1, indent.SelectionStart);

        var indented = Apply(text, indent);
        var outdent = MarkdownOutline.IndentListItem(indented, indent.SelectionStart, outdent: true, "\t")!.Value;
        Assert.Equal(text, Apply(indented, outdent));

        Assert.Null(MarkdownOutline.IndentListItem("plain", 0, outdent: false, "\t"));
        Assert.Null(MarkdownOutline.IndentListItem(text, 0, outdent: true, "\t")); // nothing to outdent
    }

    [Fact]
    public void KeepsCrLfLineEndings()
    {
        const string text = "## One\r\nfirst\r\n## Two\r\nsecond";

        Assert.Equal("## Two\r\nsecond\r\n## One\r\nfirst", Apply(text, MarkdownOutline.MoveBlock(text, text.IndexOf("## Two", StringComparison.Ordinal), up: true, "\r\n")!.Value));
    }
}
