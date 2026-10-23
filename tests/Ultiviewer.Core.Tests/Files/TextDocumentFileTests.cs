using System.Text;
using Ultiviewer.Core.Files;
using Ultiviewer.Core.Text;

namespace Ultiviewer.Core.Tests.Files;

public class TextDocumentFileTests
{
    [Fact]
    public void LoadAndSaveKeepFormatAndReportStamp()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("lf.md");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("a\nb\n")]);

        var loaded = TextDocumentFile.Load(path);
        Assert.Equal("a\nb\n", loaded.Text);
        Assert.Equal("UTF-8 BOM", loaded.Format.EncodingName);
        Assert.Equal(LineEnding.Lf, loaded.Format.LineEnding);
        Assert.Equal(FileStamp.Read(path), loaded.Stamp);

        var (stamp, hash) = TextDocumentFile.Save(path, loaded.Text + "c\n", loaded.Format);
        Assert.Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("a\nb\nc\n")], File.ReadAllBytes(path));
        Assert.Equal(TextDocumentFile.HashFile(path), hash);
        Assert.Equal(new FileInfo(path).Length, stamp.Length);
    }

    [Fact]
    public void DetectsBinaryButNotUtf16()
    {
        using var dir = new TempDirectory();
        var binary = dir.Combine("x.png");
        File.WriteAllBytes(binary, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13]);
        var utf16 = dir.Combine("u.txt");
        File.WriteAllBytes(utf16, Encoding.Unicode.GetBytes("hello world"));

        Assert.Equal(LoadProblem.Binary, TextDocumentFile.Inspect(binary));
        Assert.Equal(LoadProblem.None, TextDocumentFile.Inspect(utf16));
    }

    [Fact]
    public void BackupStoreRoundTripAndCleanup()
    {
        using var dir = new TempDirectory();
        var store = new BackupStore(dir.Combine("backup"));
        var keep = BackupStore.NewName();
        var drop = BackupStore.NewName();

        store.Write(keep, "unsaved ✓");
        store.Write(drop, "old");
        store.DeleteAllExcept([keep]);

        Assert.Equal("unsaved ✓", store.Read(keep));
        Assert.Null(store.Read(drop));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("one", 1)]
    [InlineData("  two words ", 2)]
    [InlineData("don't stop - well-known", 3)]
    [InlineData("שלום עולם, привет", 3)]
    [InlineData("# Title\n- [ ] task 12", 3)]
    public void CountsWords(string text, int expected) => Assert.Equal(expected, TextStatistics.CountWords(text));
}
