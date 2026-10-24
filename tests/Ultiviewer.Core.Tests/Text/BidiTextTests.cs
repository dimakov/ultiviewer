using Ultiviewer.Core.Text;

namespace Ultiviewer.Core.Tests.Text;

public class BidiTextTests
{
    [Theory]
    [InlineData("שלום world", true)]
    [InlineData("Hello שלום", false)]
    [InlineData("123 - שלום", true)]
    [InlineData("مرحبا", true)]
    [InlineData("12:30 !", null)]
    [InlineData("", null)]
    public void FirstStrongLetterDecides(string text, bool? expected)
    {
        Assert.Equal(expected, BidiText.FirstStrong(text));
    }

    [Theory]
    [InlineData("- [x] לקנות חלב", true)]
    [InlineData("## Plan for היום", false)]
    [InlineData("> ציטוט", true)]
    [InlineData("1. ראשון", true)]
    [InlineData("![](images/pic.png) תמונה", true)]
    [InlineData("[link](https://x.test) עם טקסט", false)]
    [InlineData("`code` ואז עברית", true)]
    [InlineData("x = 1;", false)]
    public void LineDirectionSkipsMarkdown(string line, bool? expected)
    {
        Assert.Equal(expected, BidiText.LineDirection(line));
    }

    [Fact]
    public void DocumentDirectionByMajorityOfProseLines()
    {
        const string hebrew = """
            ---
            tags: [work, plans]
            title: Weekly plan
            ---
            # תכנון שבועי

            - [ ] לסיים את הדוח
            - [x] Call Dana
            ```python
            print("hello")
            print("world")
            ```
            הערות נוספות
            """;

        Assert.True(BidiText.Detect(hebrew));
        Assert.False(BidiText.Detect("# Notes\n\nSome text\nשורה אחת"));
        Assert.True(BidiText.Detect("שורה\nline"));   // a tie goes to the first line
        Assert.Null(BidiText.Detect("123\n- [ ] \n"));
    }
}
