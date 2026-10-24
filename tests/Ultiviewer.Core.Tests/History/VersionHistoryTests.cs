using Ultiviewer.Core.History;

namespace Ultiviewer.Core.Tests.History;

public class VersionHistoryTests
{
    private static readonly DateTime T0 = new(2026, 10, 24, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RecordsReadsAndSkipsIdenticalText()
    {
        using var temp = new TempDirectory();
        var history = new VersionHistory(temp.Combine("history"));
        var page = temp.Combine("nb/Page.md");

        var first = history.Record(page, "one", VersionReason.BeforeEditing, T0)!;
        Assert.Null(history.Record(page, "one", VersionReason.Saved, T0.AddMinutes(1)));
        var second = history.Record(page, "one\ntwo", VersionReason.Saved, T0.AddMinutes(2))!;

        Assert.Equal([second.Id, first.Id], history.List(page).Select(v => v.Id));
        Assert.Equal("one", history.Read(page, first.Id));
        Assert.Equal("one\ntwo", history.Read(page, second.Id));
        Assert.Empty(history.List(temp.Combine("nb/Other.md")));
    }

    [Fact]
    public void SavesWithinTheIntervalKeepTheFirst()
    {
        using var temp = new TempDirectory();
        var history = new VersionHistory(temp.Combine("history")) { Interval = TimeSpan.FromMinutes(5) };
        var page = temp.Combine("Page.md");

        history.Record(page, "a", VersionReason.Saved, T0);
        Assert.Null(history.Record(page, "ab", VersionReason.Saved, T0.AddMinutes(2)));
        Assert.NotNull(history.Record(page, "abc", VersionReason.ChangedOnDisk, T0.AddMinutes(3))); // other reasons always count
        Assert.NotNull(history.Record(page, "abcd", VersionReason.Saved, T0.AddMinutes(4)));
        Assert.Null(history.Record(page, "abcde", VersionReason.Saved, T0.AddMinutes(6)));
        Assert.NotNull(history.Record(page, "abcdef", VersionReason.Saved, T0.AddMinutes(10)));

        Assert.Equal(4, history.List(page).Count);
    }

    [Fact]
    public void PruneKeepsRecentVersionsAndThinsOlderOnes()
    {
        var now = T0.AddDays(60);
        var versions = new List<VersionInfo>();
        void Add(DateTime time) => versions.Add(new VersionInfo(time.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), time, 1, time.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), VersionReason.Saved));

        Add(now.AddMinutes(-10));
        Add(now.AddMinutes(-20)); // both in the last day: kept
        Add(now.AddDays(-2).AddMinutes(-10));
        Add(now.AddDays(-2).AddMinutes(-20)); // same hour, 2 days ago: newest kept
        Add(now.AddDays(-40).AddHours(-1));
        Add(now.AddDays(-40).AddHours(-3)); // same day, 40 days ago: newest kept

        var kept = VersionHistory.Prune(versions, now);

        Assert.Equal([versions[0], versions[1], versions[2], versions[4]], kept);
    }

    [Fact]
    public void FollowsRenamesAndFolderMoves()
    {
        using var temp = new TempDirectory();
        var history = new VersionHistory(temp.Combine("history"));
        var page = temp.Combine("nb/Section/Page.md");
        history.Record(page, "text", VersionReason.Saved, T0);

        history.Move(temp.Combine("nb/Section"), temp.Combine("nb/Renamed"));
        var moved = temp.Combine("nb/Renamed/Page.md");
        Assert.Empty(history.List(page));
        Assert.Single(history.List(moved));

        history.Move(moved, temp.Combine("nb/Renamed/New name.md"));
        var id = Assert.Single(history.List(temp.Combine("nb/Renamed/New name.md"))).Id;
        Assert.Equal("text", history.Read(temp.Combine("nb/Renamed/New name.md"), id));
    }

    [Fact]
    public void DiffsLines()
    {
        var diff = LineDiff.Compute("a\nb\nc\nd", "a\nc\nX\nd\ne");

        Assert.Equal(
            ["  a", "- b", "  c", "+ X", "  d", "+ e"],
            diff.Select(d => (d.Kind switch { DiffKind.Added => "+ ", DiffKind.Removed => "- ", _ => "  " }) + d.Text));
        Assert.Equal((2, 1), LineDiff.Count(diff));
        Assert.Equal(3, diff.Single(d => d.Text == "X").NewLine);
        Assert.Equal(2, diff.Single(d => d.Text == "b").OldLine);
    }

    [Theory]
    [InlineData("", "a\nb")]
    [InlineData("a\nb", "")]
    [InlineData("same", "same")]
    [InlineData("x\ny\nz", "z\ny\nx")]
    public void DiffRebuildsBothTexts(string oldText, string newText)
    {
        var diff = LineDiff.Compute(oldText, newText);

        Assert.Equal(LineDiff.SplitLines(oldText), diff.Where(d => d.Kind != DiffKind.Added).Select(d => d.Text));
        Assert.Equal(LineDiff.SplitLines(newText), diff.Where(d => d.Kind != DiffKind.Removed).Select(d => d.Text));
    }

    [Fact]
    public void DiffOfLargeTextsStaysFastAndCorrect()
    {
        var random = new Random(7);
        var lines = Enumerable.Range(0, 20000).Select(i => "line " + i).ToList();
        var edited = lines.ToList();
        for (var i = 0; i < 300; i++)
        {
            edited[random.Next(edited.Count)] = "changed " + i;
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var diff = LineDiff.Compute(lines, edited);
        watch.Stop();

        Assert.Equal(edited, diff.Where(d => d.Kind != DiffKind.Removed).Select(d => d.Text));
        Assert.True(watch.ElapsedMilliseconds < 2000, $"took {watch.ElapsedMilliseconds} ms");
    }
}
