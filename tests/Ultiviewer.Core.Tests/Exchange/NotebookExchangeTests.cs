using System.IO.Compression;
using System.Text;
using Ultiviewer.Core.Exchange;
using Ultiviewer.Core.Notebooks;

namespace Ultiviewer.Core.Tests.Exchange;

public class NotebookExchangeTests
{
    private static void CreateNotebook(TempDirectory dir)
    {
        dir.File("nb/Inbox.md", "See [plan](Projects/Plan.md), [[Plan]] and [[Sub|the sub]].\n![x](Inbox.assets/a.png)\n[out](../outside.md)\n");
        dir.File("nb/Inbox.assets/a.png", "PNG");
        dir.File("nb/Projects/Plan.md", "Back to [inbox](../Inbox.md#top) and [[Inbox]].\n");
        dir.File("nb/Projects/Plan/Sub.md", "Up: [plan](../Plan.md)\n");
        dir.File("nb/Projects/notes.txt", "plain");
        NotebookOperations.SetOrder(dir.Combine("nb"), ["Inbox.md", "Projects"]);
        NotebookOperations.SetColor(dir.Combine("nb/Projects"), "#E07B39");
    }

    [Fact]
    public void ExportNumbersNamesAndRewritesLinks()
    {
        using var dir = new TempDirectory();
        CreateNotebook(dir);
        var zipPath = dir.Combine("out.zip");

        var manifest = NotebookExporter.Export(dir.Combine("nb"), "My Notes", zipPath);

        using var zip = ZipFile.OpenRead(zipPath);
        Assert.Equal(
            [
                "My Notes/01 Inbox.assets/a.png",
                "My Notes/01 Inbox.md",
                "My Notes/02 Projects/01 notes.txt",
                "My Notes/02 Projects/02 Plan.md",
                "My Notes/02 Projects/02 Plan/01 Sub.md",
                ExportManifest.FileName,
            ],
            zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));

        Assert.Equal(
            "See [plan](02%20Projects/02%20Plan.md), [[02 Plan|Plan]] and [[01 Sub|the sub]].\n![x](01%20Inbox.assets/a.png)\n[out](../outside.md)\n",
            Read(zip, "My Notes/01 Inbox.md"));
        Assert.Equal("Up: [plan](../02%20Plan.md)\n", Read(zip, "My Notes/02 Projects/02 Plan/01 Sub.md"));

        var projects = manifest.Root.Children![1];
        Assert.Equal(("section", "Projects", "#E07B39"), (projects.Kind, projects.Name, projects.Color));
        var plan = projects.Children![1];
        Assert.Equal(("page", "Plan", "My Notes/02 Projects/02 Plan"), (plan.Kind, plan.Name, plan.Subpages));
        Assert.Contains("ultiviewer-notebook-export", Read(zip, ExportManifest.FileName));
    }

    [Fact]
    public void ImportRestoresNamesOrderColorsAndContent()
    {
        using var dir = new TempDirectory();
        CreateNotebook(dir);
        var zipPath = dir.Combine("out.zip");
        NotebookExporter.Export(dir.Combine("nb"), "My Notes", zipPath);

        var imported = NotebookImporter.Import(zipPath, dir.Combine("imported"));

        Assert.Equal(dir.Combine("imported/My Notes"), imported);
        foreach (var file in new[] { "Inbox.md", "Inbox.assets/a.png", "Projects/Plan.md", "Projects/Plan/Sub.md", "Projects/notes.txt" })
        {
            Assert.Equal(File.ReadAllBytes(dir.Combine("nb/" + file)), File.ReadAllBytes(Path.Combine(imported, file)));
        }

        var original = NotebookScanner.ScanNotebook(dir.Combine("nb"));
        var restored = NotebookScanner.ScanNotebook(imported);
        Assert.Equal(original.Children.Select(Shape), restored.Children.Select(Shape));
        Assert.Equal("#E07B39", restored.Children.Single(n => n.Name == "Projects").Color);
        Assert.Empty(Directory.GetDirectories(dir.Combine("imported"), ".uv-import-*"));
    }

    [Fact]
    public void ImportWithoutManifestStripsPrefixesAndKeepsOrder()
    {
        using var dir = new TempDirectory();
        var zipPath = dir.Combine("plain.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            Write(zip, "Stuff/01 Zebra.md", "Go to [[02 Apple]] or [apple](02%20Apple.md)");
            Write(zip, "Stuff/02 Apple.md", "back to [[01 Zebra|zebra]]");
            Write(zip, "Stuff/02 Apple/1-Child.md", "child");
        }

        var imported = NotebookImporter.Import(zipPath, dir.Combine("in"));

        Assert.Equal(["Zebra", "Apple"], NotebookScanner.ScanFolder(imported).Select(n => n.Name));
        Assert.Equal("Go to [[Apple]] or [apple](Apple.md)", File.ReadAllText(Path.Combine(imported, "Zebra.md")));
        Assert.Equal("back to [[Zebra|zebra]]", File.ReadAllText(Path.Combine(imported, "Apple.md")));
        Assert.True(File.Exists(Path.Combine(imported, "Apple", "Child.md")));
    }

    [Fact]
    public void RefusesArchivesThatEscapeTheTarget()
    {
        using var dir = new TempDirectory();
        var zipPath = dir.Combine("evil.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            Write(zip, "../../evil.txt", "boom");
        }

        Assert.Throws<InvalidDataException>(() => NotebookImporter.Import(zipPath, dir.Combine("in")));
        Assert.False(File.Exists(dir.Combine("evil.txt")));
    }

    private static string Shape(NotebookNode node) =>
        node.Kind + ":" + node.Name + "[" + string.Join(",", node.Children.Select(Shape)) + "]";

    private static string Read(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void Write(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }
}
