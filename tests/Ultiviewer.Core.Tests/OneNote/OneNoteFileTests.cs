using System.Text;
using Ultiviewer.Core.OneNote;
using Ultiviewer.Core.OneNote.Store;

namespace Ultiviewer.Core.Tests.OneNote;

/// <summary>Reading OneNote files: the cabinet of a .onepkg, the notebook layout, and a real OneNote 2016 section.</summary>
public class OneNoteFileTests
{
    private static string Sample(string name) => Path.Combine(AppContext.BaseDirectory, "OneNote", "Samples", name);

    private static string BigText() =>
        string.Concat(Enumerable.Range(0, 4000).Select(i => $"{i:D5} The quick brown fox jumps over the lazy dog {i % 97}\n"));

    [Fact]
    public void ExtractsMsZipCabinetAcrossBlocksAndFolders()
    {
        using var temp = new TempDirectory();
        Cabinet.Extract(Sample("Notebook.onepkg"), temp.Path);

        Assert.Equal(BigText(), File.ReadAllText(temp.Combine("Big.txt")));
        var section = File.ReadAllBytes(Sample("OneWithFileData.one"));
        Assert.Equal(section, File.ReadAllBytes(temp.Combine("Notes.one")));
        Assert.Equal(section, File.ReadAllBytes(temp.Combine("Group/Café.one")));
        Assert.Equal("x", File.ReadAllText(temp.Combine("OneNote_RecycleBin/OneNote_DeletedPages.one")));
    }

    [Fact]
    public void RefusesCabinetNamesOutsideTheDestination()
    {
        using var temp = new TempDirectory();
        Assert.Null(Cabinet.SafeTarget(temp.Path, @"..\evil.one"));
        Assert.Null(Cabinet.SafeTarget(temp.Path, @"a\..\..\evil.one"));
        Assert.Null(Cabinet.SafeTarget(temp.Path, @"C:\Windows\evil.one"));
        Assert.Equal(temp.Combine("Group/Section.one"), Cabinet.SafeTarget(temp.Path, @"\Group\Section.one"));
    }

    [Fact]
    public void RejectsFilesThatAreNotCabinets()
    {
        using var temp = new TempDirectory();
        var path = temp.File("fake.onepkg", "PK this is a zip, not a cabinet");
        var error = Assert.Throws<OneNoteFormatException>(() => Cabinet.Extract(path, temp.Combine("out")));
        Assert.Contains("not a OneNote package", error.Message);
    }

    [Fact]
    public void OpensPackageWithSectionGroupsAndDeletesItsTemporaryFolder()
    {
        string sectionPath;
        using (var source = OneNoteSource.Open(Sample("Notebook.onepkg")))
        {
            Assert.Equal("Notebook", source.Notebook.Name);
            Assert.Collection(
                source.Notebook.Entries,
                group =>
                {
                    var g = Assert.IsType<OneNoteSectionGroup>(group);
                    Assert.Equal("Group", g.Name);
                    Assert.Equal("Café", Assert.IsType<OneNoteSectionFile>(Assert.Single(g.Entries)).Name);
                },
                section => Assert.Equal("Notes", Assert.IsType<OneNoteSectionFile>(section).Name)); // the recycle bin is left out

            Assert.Equal(2, OneNoteSource.CountSections(source.Notebook.Entries));
            sectionPath = ((OneNoteSectionFile)source.Notebook.Entries[1]).Path;
            Assert.True(File.Exists(sectionPath));
        }

        Assert.False(File.Exists(sectionPath));
    }

    [Fact]
    public void OpensSingleSectionFile()
    {
        using var source = OneNoteSource.Open(Sample("OneWithFileData.one"));
        Assert.Equal("OneWithFileData", source.Notebook.Name);
        Assert.Equal("OneWithFileData", Assert.IsType<OneNoteSectionFile>(Assert.Single(source.Notebook.Entries)).Name);
    }

    [Fact]
    public void ReadsOneNote2016SectionWithEmbeddedFile()
    {
        var section = OneNoteReader.ReadSection(Sample("OneWithFileData.one"));

        Assert.Null(section.Problem);
        var page = Assert.Single(section.Pages);
        Assert.Equal("", page.Title);
        Assert.Equal(1, page.Level);
        Assert.Equal(new DateTime(2019, 3, 14), page.Created!.Value.ToUniversalTime().Date);

        var file = page.Blocks.SelectMany(Contents).OfType<OneNoteFile>().Single();
        Assert.Equal("testing.docx", file.Name);
        Assert.Equal(14660, file.Data!.Length);
        Assert.Equal("PK"u8.ToArray(), file.Data[..2]); // a .docx is a zip
    }

    [Fact]
    public void ReportsFilesThatAreNotOneNoteSections()
    {
        using var temp = new TempDirectory();
        var path = temp.File("notes.one", new string('x', 2048));
        var section = OneNoteReader.ReadSection(path);
        Assert.Empty(section.Pages);
        Assert.NotNull(section.Problem);
    }

    [Fact]
    public void RecognizesTheOneDriveFormat()
    {
        var header = new byte[1024];
        RevisionStoreReader.PackagedFormat.TryWriteBytes(header.AsSpan(48));
        Assert.Equal(RevisionStoreReader.PackagedFormat, RevisionStoreReader.FormatOf(header));
        Assert.Equal(RevisionStoreReader.DesktopFormat, RevisionStoreReader.FormatOf(File.ReadAllBytes(Sample("OneWithFileData.one"))));
        Assert.Null(RevisionStoreReader.FormatOf(Encoding.ASCII.GetBytes("not a OneNote file")));
    }

    private static IEnumerable<OneNoteContent> Contents(OneNoteBlock block) => block switch
    {
        OneNoteFreeContent free => [free.Content],
        OneNoteOutline outline => outline.Elements.SelectMany(Flatten),
        _ => [],
    };

    private static IEnumerable<OneNoteContent> Flatten(OneNoteElement element) =>
        element.Contents.Concat(element.Children.SelectMany(Flatten));
}
