using Ultiviewer.Core.Notebooks;

namespace Ultiviewer.Core.Tests.Notebooks;

public class NotebookOperationsTests
{
    private readonly NotebookOperations _ops = new(new PermanentFileDeleter());

    [Fact]
    public void CreatePageAndSection()
    {
        using var dir = new TempDirectory();

        var section = _ops.CreateSection(dir.Path, "Work");
        var page = _ops.CreatePage(section, "Plan", "# Plan");

        Assert.True(Directory.Exists(section));
        Assert.Equal("# Plan", File.ReadAllText(page));
        Assert.Throws<NotebookOperationException>(() => _ops.CreatePage(section, "plan"));
        Assert.Throws<NotebookOperationException>(() => _ops.CreatePage(dir.Path, "Work"));
        Assert.Throws<NotebookOperationException>(() => _ops.CreateSection(section, "Plan"));
    }

    [Fact]
    public void NewPageGoesAfterSelectionWhenFolderHasManualOrder()
    {
        using var dir = new TempDirectory();
        dir.File("a.md");
        dir.File("b.md");
        NotebookOperations.SetOrder(dir.Path, ["b.md", "a.md"]);

        _ops.CreatePage(dir.Path, "c", insertAfterEntry: "b.md");

        Assert.Equal(["b.md", "c.md", "a.md"], NotebookScanner.ListEntries(dir.Path));
    }

    [Fact]
    public void UniqueNameSkipsPagesAndSections()
    {
        using var dir = new TempDirectory();
        dir.File("Untitled.md");
        dir.Folder("Untitled 2");

        Assert.Equal("Untitled 3", NotebookOperations.UniqueName(dir.Path, "Untitled"));
    }

    [Fact]
    public void RenamePageMovesSubpagesAttachmentsAndRewritesOwnLinks()
    {
        using var dir = new TempDirectory();
        var page = dir.File("Old name.md", "![shot](Old%20name.assets/a.png)\n[x](<Old name.assets/b.pdf>)\n[web](https://example.com/Old%20name.assets/a.png)");
        dir.File("Old name/Child.md");
        dir.File("Old name.assets/a.png");
        NotebookOperations.SetOrder(dir.Path, ["Old name.md"]);
        var node = Page(page);

        var renamed = _ops.Rename(node, "New name");

        Assert.Equal(dir.Combine("New name.md"), renamed);
        Assert.True(File.Exists(dir.Combine("New name/Child.md")));
        Assert.True(File.Exists(dir.Combine("New name.assets/a.png")));
        Assert.False(Directory.Exists(dir.Combine("Old name")));
        Assert.Equal(
            "![shot](New%20name.assets/a.png)\n[x](<New name.assets/b.pdf>)\n[web](https://example.com/Old%20name.assets/a.png)",
            File.ReadAllText(renamed));
        Assert.Equal(["New name.md"], FolderMetadataStore.Read(dir.Path).Order);
    }

    [Fact]
    public void CaseOnlyRenameWorks()
    {
        using var dir = new TempDirectory();
        var section = dir.Folder("work");

        var renamed = _ops.Rename(Section(section), "Work");

        Assert.Equal("Work", new DirectoryInfo(renamed).Name);
        Assert.Equal("Work", Assert.Single(Directory.GetDirectories(dir.Path).Select(Path.GetFileName)));
    }

    [Fact]
    public void MovePageIntoAnotherSectionAtIndex()
    {
        using var dir = new TempDirectory();
        var page = dir.File("A/Page.md");
        dir.File("A/Page/Sub.md");
        dir.File("B/One.md");
        dir.File("B/Two.md");
        NotebookOperations.SetOrder(dir.Combine("A"), ["Page.md"]);

        var moved = _ops.Move(Page(page), dir.Combine("B"), index: 1);

        Assert.Equal(dir.Combine("B/Page.md"), moved);
        Assert.True(File.Exists(dir.Combine("B/Page/Sub.md")));
        Assert.Empty(FolderMetadataStore.Read(dir.Combine("A")).Order);
        Assert.Equal(["One.md", "Page.md", "Two.md"], NotebookScanner.ListEntries(dir.Combine("B")));
    }

    [Fact]
    public void ReorderWithinFolder()
    {
        using var dir = new TempDirectory();
        dir.File("a.md");
        dir.File("b.md");
        var c = dir.File("c.md");

        _ops.Move(Page(c), dir.Path, index: 0);

        Assert.Equal(["c.md", "a.md", "b.md"], NotebookScanner.ListEntries(dir.Path));
    }

    [Fact]
    public void CannotMoveIntoItself()
    {
        using var dir = new TempDirectory();
        var section = dir.Folder("S");
        dir.Folder("S/Inner");
        var page = dir.File("P.md");
        dir.File("P/Child.md");

        Assert.Throws<NotebookOperationException>(() => _ops.Move(Section(section), dir.Combine("S/Inner"), null));
        Assert.Throws<NotebookOperationException>(() => _ops.Move(Page(page), dir.Combine("P"), null));
    }

    [Fact]
    public void DeletePageRemovesEverythingAndOrderEntry()
    {
        using var dir = new TempDirectory();
        var page = dir.File("P.md");
        dir.File("P/Child.md");
        dir.File("P.assets/x.png");
        dir.File("Q.md");
        NotebookOperations.SetOrder(dir.Path, ["Q.md", "P.md"]);

        _ops.Delete(Page(page));

        Assert.Equal(["Q.md"], NotebookScanner.ListEntries(dir.Path));
        Assert.False(Directory.Exists(dir.Combine("P")));
        Assert.False(Directory.Exists(dir.Combine("P.assets")));
        Assert.Equal(["Q.md"], FolderMetadataStore.Read(dir.Path).Order);
    }

    private static NotebookNode Page(string path) => new()
    {
        Kind = NodeKind.Page,
        Name = Path.GetFileNameWithoutExtension(path),
        Path = path,
    };

    private static NotebookNode Section(string path) => new()
    {
        Kind = NodeKind.Section,
        Name = Path.GetFileName(path),
        Path = path,
    };
}
