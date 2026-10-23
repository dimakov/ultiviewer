using Ultiviewer.Core.Notebooks;

namespace Ultiviewer.Core.Tests.Notebooks;

public class NamingTests
{
    [Theory]
    [InlineData("Day 2", "Day 10", -1)]
    [InlineData("day 2", "Day 2", 1)]
    [InlineData("a", "B", -1)]
    [InlineData("file1", "file01", 1)]
    [InlineData("x", "x", 0)]
    public void NaturalCompare(string a, string b, int expectedSign) =>
        Assert.Equal(expectedSign, Math.Sign(NaturalStringComparer.Instance.Compare(a, b)));

    [Theory]
    [InlineData("Meeting notes")]
    [InlineData("שלום")]
    [InlineData("v1.2 plan")]
    public void ValidNames(string name) => Assert.Null(FileNameRules.Validate(name));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("what?")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData(".hidden")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("Page.assets")]
    public void InvalidNames(string name) => Assert.NotNull(FileNameRules.Validate(name));
}
