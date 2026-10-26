using Ultiviewer.Core.Notebooks;
using Ultiviewer.Core.OneNote;

namespace Ultiviewer.Core.Tests.OneNote;

public class OneNoteImporterTests
{
    private static string Sample(string name) => Path.Combine(AppContext.BaseDirectory, "OneNote", "Samples", name);

    private static OneNotePage Page(string title, int level) => new(title, level, null, null, []);

    [Fact]
    public void ArrangesSubpagesUnderThePagesBeforeThem()
    {
        var pages = new[] { Page("Lone sub", 2), Page("A", 1), Page("A.1", 2), Page("A.1.a", 3), Page("A.2", 2), Page("B", 1), Page("B deep", 3) };

        var tree = OneNoteImporter.Arrange(pages);

        Assert.Equal(["Lone sub", "A", "B"], tree.Select(n => n.Page.Title));
        Assert.Equal(["A.1", "A.2"], tree[1].Children.Select(n => n.Page.Title));
        Assert.Equal("A.1.a", Assert.Single(tree[1].Children[0].Children).Page.Title);
        Assert.Equal("B deep", Assert.Single(tree[2].Children).Page.Title); // a level-3 page right after a level-1 page
    }

    [Fact]
    public void ImportsPackageAsNotebookFolderWithAttachmentsOrderAndTimes()
    {
        using var temp = new TempDirectory();
        temp.Folder("Notebook"); // taken: the import gets "Notebook 2"
        using var source = OneNoteSource.Open(Sample("Notebook.onepkg"));
        var reported = new List<string>();

        var result = OneNoteImporter.Import(source.Notebook, temp.Path, source.Notebook.Name, progress: new SyncProgress(reported.Add));

        Assert.Equal(temp.Combine("Notebook 2"), result.Folder);
        Assert.Equal((2, 2, 2), (result.Sections, result.Pages, result.Attachments));
        Assert.Empty(result.Issues);
        Assert.Equal(["Group › Café › Untitled page", "Notes › Untitled page"], reported);

        var page = temp.Combine("Notebook 2/Notes/Untitled page.md");
        var original = OneNoteReader.ReadSection(Sample("OneWithFileData.one")).Pages[0];
        Assert.Equal(
            $"---\ncreated: {original.Created!.Value.ToLocalTime():yyyy-MM-dd HH:mm}\nmodified: {original.Modified!.Value.ToLocalTime():yyyy-MM-dd HH:mm}\n"
            + "source: OneNote\n---\n\n[testing.docx](Untitled%20page.assets/testing.docx)\n",
            File.ReadAllText(page));
        Assert.Equal(14660, new FileInfo(temp.Combine("Notebook 2/Notes/Untitled page.assets/testing.docx")).Length);
        Assert.Equal(2019, File.GetLastWriteTimeUtc(page).Year);
        Assert.True(File.Exists(temp.Combine("Notebook 2/Group/Café/Untitled page.md")));
        Assert.False(Directory.Exists(temp.Combine("Notebook 2/OneNote_RecycleBin")));
        Assert.Equal(["Group", "Notes"], FolderMetadataStore.Read(result.Folder).Order);
        Assert.Empty(Directory.GetDirectories(temp.Path, ".uv-import-*"));
    }

    [Fact]
    public void ImportsOnlyChosenSectionsAndReportsUnreadableOnes()
    {
        using var temp = new TempDirectory();
        var folder = temp.Folder("Source");
        File.Copy(Sample("OneWithFileData.one"), Path.Combine(folder, "Kept.one"));
        File.Copy(Sample("OneWithFileData.one"), Path.Combine(folder, "Skipped.one"));
        File.WriteAllText(Path.Combine(temp.Folder("Source/Group"), "Broken.one"), new string('x', 4096));
        var notebook = OneNoteSource.ReadNotebook(folder, "Mine");

        var result = OneNoteImporter.Import(notebook, temp.Combine("Out"), "Mine", new OneNoteImportOptions { IncludeSection = s => s.Name != "Skipped" });

        Assert.Equal(1, result.Sections);
        Assert.True(Directory.Exists(temp.Combine("Out/Mine/Kept")));
        Assert.False(Directory.Exists(temp.Combine("Out/Mine/Skipped")));
        Assert.False(Directory.Exists(temp.Combine("Out/Mine/Group"))); // its only section couldn't be read
        var issue = Assert.Single(result.Issues);
        Assert.Equal("Group › Broken", issue.Where);
        Assert.StartsWith("Not imported: ", issue.Message);
    }

    [Fact]
    public void ImportsChosenPagesPromotesSubpagesAndRepointsPageLinks()
    {
        using var temp = new TempDirectory();
        var link = OneNoteStyle.Plain with { Hyperlink = true };
        const string target = "﷟HYPERLINK \"onenote:Work.one#Q1%3A%20Plan&section-id={1}&end\"";
        var linking = new OneNoteText(target + "the plan", [new(0, target.Length, link with { Hidden = true }), new(target.Length, target.Length + 8, link)], null, []);
        OneNotePage[] pages =
        [
            new("Q1: Plan", 1, null, null, []),
            new("Skipped", 1, null, null, []),
            new("Kept child", 2, null, null, [new OneNoteOutline([new OneNoteElement([linking], [], null)])]),
            new("Dropped child", 2, null, null, []),
        ];
        var options = new OneNoteImportOptions
        {
            ReadSection = _ => new OneNoteSection("Work", pages),
            IncludePage = (_, index) => index is 0 or 2,
        };

        var result = OneNoteImporter.Import(new OneNoteNotebook("NB", [new OneNoteSectionFile("Work", "Work.one")]), temp.Path, "NB", options);

        Assert.Equal(2, result.Pages);
        Assert.Equal(["Q1 - Plan.md", "Kept child.md"], FolderMetadataStore.Read(temp.Combine("NB/Work")).Order);
        Assert.False(Directory.Exists(temp.Combine("NB/Work/Skipped")));
        Assert.EndsWith("\n[[Q1 - Plan|the plan]]\n", File.ReadAllText(temp.Combine("NB/Work/Kept child.md")));
    }

    [Fact]
    public void CancellingLeavesNothingBehind()
    {
        using var temp = new TempDirectory();
        using var source = OneNoteSource.Open(Sample("Notebook.onepkg"));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => OneNoteImporter.Import(source.Notebook, temp.Path, "Notebook", cancellation: cancel.Token));
        Assert.Empty(Directory.GetFileSystemEntries(temp.Path));
    }

    /// <summary>Reports on the calling thread (<see cref="Progress{T}"/> posts to the thread pool).</summary>
    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
