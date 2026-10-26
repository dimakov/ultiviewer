using Ultiviewer.Core.Links;
using Ultiviewer.Core.Notebooks;

namespace Ultiviewer.Core.OneNote;

/// <summary>What an import wrote, and what it couldn't convert (per section or page).</summary>
public sealed record OneNoteImportResult(string Folder, int Sections, int Pages, int Attachments, IReadOnlyList<OneNoteImportIssue> Issues);

/// <param name="Where">"Section" or "Section › Page".</param>
public sealed record OneNoteImportIssue(string Where, string Message);

/// <summary>What to import: everything by default.</summary>
public sealed class OneNoteImportOptions
{
    /// <summary>The sections to import.</summary>
    public Func<OneNoteSectionFile, bool> IncludeSection { get; init; } = _ => true;

    /// <summary>
    /// The pages to import, by section and index in <see cref="OneNoteSection.Pages"/>. The checked subpages of a
    /// page that is left out take its place.
    /// </summary>
    public Func<OneNoteSectionFile, int, bool> IncludePage { get; init; } = (_, _) => true;

    /// <summary>Reads a section; the import preview passes the sections it has already read.</summary>
    public Func<OneNoteSectionFile, OneNoteSection> ReadSection { get; init; } = file => OneNoteReader.ReadSection(file.Path);
}

/// <summary>
/// Writes a OneNote notebook as a new Ultiviewer notebook folder: section groups and sections become folders,
/// pages become <c>Title.md</c> (subpages in the <c>Title/</c> folder next to their parent page, pictures and
/// attachments in <c>Title.assets/</c>), in OneNote's order. Page files carry OneNote's created and modified
/// times. The notebook is built in a hidden staging folder and moved into place at the end, so a failed or
/// cancelled import leaves nothing behind.
/// </summary>
public static class OneNoteImporter
{
    private const string UntitledPage = "Untitled page";

    /// <param name="progress">Receives "Section › Page" as each page is written.</param>
    public static OneNoteImportResult Import(
        OneNoteNotebook notebook, string destinationParent, string name,
        OneNoteImportOptions? options = null, IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        Directory.CreateDirectory(destinationParent);
        var staging = Path.Combine(destinationParent, ".uv-import-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var notebookName = FileNameRules.MakeValid(name, "OneNote notebook");
            var top = Path.Combine(staging, notebookName);
            Directory.CreateDirectory(top);
            var writer = new Writer(options ?? new OneNoteImportOptions(), progress, cancellation);
            writer.WriteEntries(notebook.Entries, top, "");
            writer.FixWikiLinks();

            var destination = Path.Combine(destinationParent, NotebookOperations.UniqueName(destinationParent, notebookName));
            Directory.Move(top, destination);
            return new OneNoteImportResult(destination, writer.Sections, writer.Pages.Count, writer.Attachments, writer.Issues);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    /// <summary>
    /// Arranges a section's pages as OneNote shows them: a level-2 page is a subpage of the level-1 page before it,
    /// a level-3 page of the level-2 page before it. A subpage without a parent stays at the top.
    /// </summary>
    public static IReadOnlyList<PageNode> Arrange(IReadOnlyList<OneNotePage> pages)
    {
        var top = new List<PageNode>();
        var parents = new PageNode?[4];
        for (var index = 0; index < pages.Count; index++)
        {
            var level = Math.Clamp(pages[index].Level, 1, 3);
            while (level > 1 && parents[level - 1] is null)
            {
                level--;
            }

            var node = new PageNode(pages[index], index, []);
            (level == 1 ? top : parents[level - 1]!.Children).Add(node);
            parents[level] = node;
            for (var deeper = level + 1; deeper < parents.Length; deeper++)
            {
                parents[deeper] = null;
            }
        }

        return top;
    }

    /// <param name="Index">The page's index in <see cref="OneNoteSection.Pages"/>.</param>
    public sealed record PageNode(OneNotePage Page, int Index, List<PageNode> Children);

    private sealed class Writer(OneNoteImportOptions options, IProgress<string>? progress, CancellationToken cancellation)
    {
        // Wiki link target written by the converter (from the OneNote title) → the page's file name, when they differ.
        private readonly Dictionary<string, string> _renamed = new(StringComparer.OrdinalIgnoreCase);

        public int Sections { get; private set; }

        public List<string> Pages { get; } = [];

        public int Attachments { get; private set; }

        public List<OneNoteImportIssue> Issues { get; } = [];

        public void WriteEntries(IReadOnlyList<OneNoteEntry> entries, string container, string where)
        {
            var order = new List<string>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                cancellation.ThrowIfCancellationRequested();
                switch (entry)
                {
                    case OneNoteSectionGroup group when Includes(group):
                    {
                        var folder = UniqueName(used, FileNameRules.MakeValid(group.Name, "Section group"));
                        var path = Directory.CreateDirectory(Path.Combine(container, folder)).FullName;
                        var before = Sections;
                        WriteEntries(group.Entries, path, Join(where, group.Name));
                        if (Sections == before)
                        {
                            Directory.Delete(path, recursive: true); // none of its sections could be read
                            used.Remove(folder);
                        }
                        else
                        {
                            order.Add(folder);
                        }

                        break;
                    }

                    case OneNoteSectionFile file when options.IncludeSection(file):
                    {
                        var section = options.ReadSection(file);
                        var label = Join(where, file.Name);
                        if (section.Problem is not null)
                        {
                            Issues.Add(new OneNoteImportIssue(label, "Not imported: " + section.Problem));
                            break;
                        }

                        var folder = UniqueName(used, FileNameRules.MakeValid(file.Name, "Section"));
                        var path = Directory.CreateDirectory(Path.Combine(container, folder)).FullName;
                        WritePages(file, Arrange(section.Pages), path, label);
                        order.Add(folder);
                        Sections++;
                        break;
                    }
                }
            }

            if (order.Count > 1)
            {
                NotebookOperations.SetOrder(container, order);
            }
        }

        private bool Includes(OneNoteSectionGroup group) =>
            group.Entries.Any(e => e switch { OneNoteSectionFile file => options.IncludeSection(file), OneNoteSectionGroup inner => Includes(inner), _ => false });

        private void WritePages(OneNoteSectionFile section, IReadOnlyList<PageNode> pages, string container, string where)
        {
            var order = new List<string>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddPages(section, pages, container, where, order, used);
            if (order.Count > 1)
            {
                NotebookOperations.SetOrder(container, order);
            }
        }

        private void AddPages(OneNoteSectionFile section, IReadOnlyList<PageNode> pages, string container, string where, List<string> order, HashSet<string> used)
        {
            foreach (var node in pages)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!options.IncludePage(section, node.Index))
                {
                    AddPages(section, node.Children, container, where, order, used); // its chosen subpages move up
                    continue;
                }

                var page = node.Page;
                var name = OneNoteReader.DisplayTitle(page);
                var title = UniqueName(used, FileNameRules.MakeValid(name, UntitledPage));
                var pagePath = Path.Combine(container, title + NotebookScanner.PageExtension);
                var label = Join(where, name.Length > 0 ? name : UntitledPage);
                progress?.Report(label);

                var assets = PageAssets.FolderFor(pagePath);
                var converted = OneNoteMarkdown.Convert(page, asset => PageAssets.RelativeUrl(pagePath, Path.Combine(assets, asset)));
                if (converted.Assets.Count > 0)
                {
                    Directory.CreateDirectory(assets);
                    foreach (var asset in converted.Assets)
                    {
                        File.WriteAllBytes(Path.Combine(assets, asset.Name), asset.Data);
                        Attachments++;
                    }
                }

                File.WriteAllText(pagePath, converted.Markdown, NotebookOperations.NewFileEncoding);
                SetTimes(pagePath, page);
                Issues.AddRange(converted.Notes.Select(note => new OneNoteImportIssue(label, note)));
                Pages.Add(pagePath);
                order.Add(title + NotebookScanner.PageExtension);

                var target = OneNoteMarkdown.WikiTarget(page.Title);
                if (target.Length > 0 && !target.Equals(title, StringComparison.Ordinal))
                {
                    _renamed.TryAdd(target, title);
                }

                if (node.Children.Any(child => Contains(section, child)))
                {
                    WritePages(section, node.Children, Directory.CreateDirectory(Path.Combine(container, title)).FullName, label);
                }
            }
        }

        private bool Contains(OneNoteSectionFile section, PageNode node) =>
            options.IncludePage(section, node.Index) || node.Children.Any(child => Contains(section, child));

        /// <summary>Links to OneNote pages were written with the OneNote title; they now point at the page files.</summary>
        public void FixWikiLinks()
        {
            if (_renamed.Count == 0)
            {
                return;
            }

            foreach (var page in Pages)
            {
                var text = File.ReadAllText(page);
                if (!text.Contains("[[", StringComparison.Ordinal))
                {
                    continue;
                }

                var (renamed, count) = WikiLinkRenamer.Rename(text, t => _renamed.GetValueOrDefault(t), keepOldTitleAsLabel: true);
                if (count > 0)
                {
                    var times = (File.GetCreationTimeUtc(page), File.GetLastWriteTimeUtc(page));
                    File.WriteAllText(page, renamed, NotebookOperations.NewFileEncoding);
                    File.SetCreationTimeUtc(page, times.Item1);
                    File.SetLastWriteTimeUtc(page, times.Item2);
                }
            }
        }

        private static void SetTimes(string path, OneNotePage page)
        {
            try
            {
                if (page.Created is { } created)
                {
                    File.SetCreationTimeUtc(path, created.ToUniversalTime());
                }

                if ((page.Modified ?? page.Created) is { } modified)
                {
                    File.SetLastWriteTimeUtc(path, modified.ToUniversalTime());
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
            {
                // The file keeps the import time.
            }
        }

        /// <summary><paramref name="name"/>, or "name 2", "name 3"… when a page or folder in this container has it.</summary>
        private static string UniqueName(HashSet<string> used, string name)
        {
            var candidate = name;
            for (var n = 2; !used.Add(candidate); n++)
            {
                candidate = $"{name} {n}";
            }

            return candidate;
        }

        private static string Join(string where, string name) => where.Length == 0 ? name : where + " › " + name;
    }
}
