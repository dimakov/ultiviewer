using Ultiviewer.Core.Markdown;

namespace Ultiviewer.Core.Tests.Markdown;

public class TaskMarkersTests
{
    [Theory]
    [InlineData("- [ ] buy milk", "- [x] buy milk")]
    [InlineData("  * [X] done", "  * [ ] done")]
    [InlineData("1. [ ] first", "1. [x] first")]
    [InlineData("plain text", "plain text")]
    [InlineData("- [link](x)", "- [link](x)")]
    public void Toggle(string line, string expected) => Assert.Equal(expected, TaskMarkers.Toggle(line));

    [Theory]
    [InlineData("call Bob", "- [ ] call Bob")]
    [InlineData("    indented", "    - [ ] indented")]
    [InlineData("- bullet", "- [ ] bullet")]
    [InlineData("- [ ] open", "- [x] open")]
    [InlineData("- [x] done", "- done")]
    [InlineData("", "- [ ] ")]
    public void Cycle(string line, string expected) => Assert.Equal(expected, TaskMarkers.Cycle(line));

    [Fact]
    public void Detects()
    {
        Assert.True(TaskMarkers.IsTask("- [x] a", out var done) && done);
        Assert.True(TaskMarkers.IsTask("+ [ ] a", out done) && !done);
        Assert.False(TaskMarkers.IsTask("- a", out _));
    }
}
