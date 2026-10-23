using Ultiviewer.Core.Notebooks;

namespace Ultiviewer.Core.Tests.Notebooks;

public class NotebookScannerTests
{
    [Fact]
    public void MapsFoldersPagesSubpagesAndFiles()
    {
        using var dir = new TempDirectory();
        dir.File("Inbox.md");
        dir.File("Projects/Ultiviewer.md");
        dir.File("Projects/Ultiviewer/Storage design.md");
        dir.File("Projects/Ultiviewer.assets/shot.png");
        dir.File("Projects/notes.txt");
        dir.File(".hidden.md");
        dir.File("Recipes/Bread.md");

        var root = NotebookScanner.ScanNotebook(dir.Path, "Personal");

        Assert.Equal(NodeKind.Notebook, root.Kind);
        Assert.Equal("Personal", root.Name);
        Assert.Equal(["Projects", "Recipes", "Inbox"], root.Children.Select(n => n.Name));

        var projects = root.Children[0];
        Assert.Equal(NodeKind.Section, projects.Kind);
        Assert.Equal(["notes.txt", "Ultiviewer"], projects.Children.Select(n => n.Name));

        var page = projects.Children.Single(n => n.Name == "Ultiviewer");
        Assert.Equal(NodeKind.Page, page.Kind);
        Assert.Equal(dir.Combine("Projects/Ultiviewer"), page.ChildFolder);
        Assert.Equal(dir.Combine("Projects/Ultiviewer.assets"), page.AssetsFolder);
        Assert.Equal("Storage design", Assert.Single(page.Children).Name);

        Assert.Equal(NodeKind.File, projects.Children.Single(n => n.Name == "notes.txt").Kind);
    }

    [Fact]
    public void AppliesStoredOrderThenSortsTheRest()
    {
        using var dir = new TempDirectory();
        dir.File("a.md");
        dir.File("b.md");
        dir.File("Day 10.md");
        dir.File("Day 2.md");
        dir.Folder("Section");
        FolderMetadataStore.Write(dir.Path, new FolderMetadata { Order = ["b.md", "missing.md", "SECTION"] });

        var names = NotebookScanner.ScanFolder(dir.Path).Select(n => n.Name);

        Assert.Equal(["b", "Section", "a", "Day 2", "Day 10"], names);
    }

    [Fact]
    public void SidecarIsHiddenAndReadsColor()
    {
        using var dir = new TempDirectory();
        var section = dir.Folder("Work");
        NotebookOperations.SetColor(section, "#3A7BD5");

        var root = NotebookScanner.ScanNotebook(dir.Path);

        Assert.Equal("#3A7BD5", Assert.Single(root.Children).Color);
        Assert.Empty(root.Children[0].Children);
        Assert.True(File.GetAttributes(Path.Combine(section, FolderMetadataStore.FileName)).HasFlag(FileAttributes.Hidden));
    }

    [Fact]
    public void MissingFolderScansAsEmpty()
    {
        Assert.Empty(NotebookScanner.ScanFolder(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())));
    }
}
