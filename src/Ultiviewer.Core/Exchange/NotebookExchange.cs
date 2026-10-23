using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ultiviewer.Core.Files;
using Ultiviewer.Core.Links;
using Ultiviewer.Core.Notebooks;
using Ultiviewer.Core.Search;
using Ultiviewer.Core.Settings;

namespace Ultiviewer.Core.Exchange;

/// <summary>
/// <c>ultiviewer-export.json</c> at the root of an export zip: the notebook hierarchy in display order with the
/// original names, so the export can be imported back exactly (and read by other tools).
/// </summary>
public sealed class ExportManifest
{
    public const string FileName = "ultiviewer-export.json";
    public const string FormatName = "ultiviewer-notebook-export";

    public string Format { get; set; } = FormatName;

    public int Version { get; set; } = 1;

    /// <summary>Notebook (or section) name.</summary>
    public string Name { get; set; } = "";

    public DateTime ExportedUtc { get; set; }

    public string Generator { get; set; } = $"{AppInfo.Name}";

    public ExportItem Root { get; set; } = new();
}

/// <summary>One notebook item. Paths are inside the zip, '/'-separated, with the order prefix ("02 Projects/01 Plan.md").</summary>
public sealed class ExportItem
{
    /// <summary><c>notebook</c>, <c>section</c>, <c>page</c> or <c>file</c>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>Original name: page title, section folder name or file name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Path in the zip: the folder for notebooks/sections, the .md file for pages, the file for files.</summary>
    public string Path { get; set; } = "";

    /// <summary>Original path relative to the exported folder.</summary>
    public string? Source { get; set; }

    /// <summary>Pages with subpages: path of the subpage folder in the zip.</summary>
    public string? Subpages { get; set; }

    /// <summary>Pages with attachments: path of the <c>Title.assets</c> folder in the zip.</summary>
    public string? Attachments { get; set; }

    public string? Color { get; set; }

    public DateTime? ModifiedUtc { get; set; }

    public List<string>? Tags { get; set; }

    public List<ExportItem>? Children { get; set; }
}

/// <summary>
/// Exports a notebook (or section) folder to a zip whose names carry the manual order as a numeric prefix
/// ("01 Inbox.md", "02 Projects/…"), so tools that sort by name show the same order. Links between exported
/// pages are rewritten to the new names; <c>[[wiki links]]</c> keep the old title as their label.
/// </summary>
public static class NotebookExporter
{
    public static ExportManifest Export(string folder, string name, string zipPath, bool isNotebook = true)
    {
        var sourceRoot = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(folder));
        var tree = NotebookScanner.ScanNotebook(sourceRoot, name);
        var topFolder = SafeName(name);

        // Path arithmetic only: the export's layout as if it were unpacked here. Nothing is written there.
        var virtualRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uv-export-" + Guid.NewGuid().ToString("N")[..8], topFolder);
        var plan = new ExportPlan(sourceRoot, virtualRoot);
        plan.Moves.Add(new PathMove(sourceRoot, virtualRoot));

        var root = new ExportItem { Kind = isNotebook ? "notebook" : "section", Name = name, Path = topFolder, Color = tree.Color };
        Number(tree, virtualRoot, topFolder, root, plan);

        var manifest = new ExportManifest { Name = name, ExportedUtc = DateTime.UtcNow, Root = root };
        var temp = zipPath + NotebookScanner.TempSuffix;
        try
        {
            using (var stream = File.Create(temp))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var page in plan.Pages)
                {
                    WritePage(zip, page, plan);
                }

                foreach (var (source, zipPath2) in plan.Files)
                {
                    zip.CreateEntryFromFile(source, zipPath2, CompressionLevel.Optimal);
                }

                foreach (var (source, zipFolder) in plan.Folders)
                {
                    AddFolder(zip, source, zipFolder);
                }

                var entry = zip.CreateEntry(ExportManifest.FileName, CompressionLevel.Optimal);
                using var json = entry.Open();
                JsonSerializer.Serialize(json, manifest, JsonFileStore.Options);
            }

            File.Move(temp, zipPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }

        return manifest;
    }

    private sealed class ExportPlan(string sourceRoot, string virtualRoot)
    {
        public string SourceRoot { get; } = sourceRoot;

        public string VirtualRoot { get; } = virtualRoot;

        /// <summary>Renames in application order (a parent before its children), on virtual paths.</summary>
        public List<PathMove> Moves { get; } = [];

        public List<(string Source, string Target, string ZipPath, ExportItem Item)> Pages { get; } = [];

        public List<(string Source, string ZipPath)> Files { get; } = [];

        public List<(string Source, string ZipFolder)> Folders { get; } = [];

        /// <summary>Original page path → numbered title, for wiki links.</summary>
        public Dictionary<string, string> Titles { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static void Number(NotebookNode node, string virtualFolder, string zipFolder, ExportItem item, ExportPlan plan)
    {
        if (node.Children.Count == 0)
        {
            return;
        }

        var width = Math.Max(2, node.Children.Count.ToString().Length);
        item.Children = [];
        for (var i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            var prefix = (i + 1).ToString().PadLeft(width, '0') + " ";
            var source = Relative(plan.SourceRoot, child.Path);
            switch (child.Kind)
            {
                case NodeKind.Section:
                {
                    var name = prefix + child.Name;
                    var target = System.IO.Path.Combine(virtualFolder, name);
                    plan.Moves.Add(new PathMove(System.IO.Path.Combine(virtualFolder, child.EntryName), target));
                    var section = new ExportItem { Kind = "section", Name = child.Name, Path = zipFolder + "/" + name, Source = source, Color = child.Color };
                    Number(child, target, section.Path, section, plan);
                    item.Children.Add(section);
                    break;
                }

                case NodeKind.Page:
                {
                    var title = prefix + child.Name;
                    var markdown = title + NotebookScanner.PageExtension;
                    var target = System.IO.Path.Combine(virtualFolder, markdown);
                    plan.Moves.Add(new PathMove(System.IO.Path.Combine(virtualFolder, child.EntryName), target));
                    plan.Titles[child.Path] = title;
                    var page = new ExportItem
                    {
                        Kind = "page",
                        Name = child.Name,
                        Path = zipFolder + "/" + markdown,
                        Source = source,
                        ModifiedUtc = File.GetLastWriteTimeUtc(child.Path),
                    };

                    if (Directory.Exists(child.AssetsFolder))
                    {
                        var assets = title + NotebookScanner.AssetsSuffix;
                        plan.Moves.Add(new PathMove(System.IO.Path.Combine(virtualFolder, child.Name + NotebookScanner.AssetsSuffix), System.IO.Path.Combine(virtualFolder, assets)));
                        page.Attachments = zipFolder + "/" + assets;
                        plan.Folders.Add((child.AssetsFolder!, page.Attachments));
                    }

                    if (child.Children.Count > 0)
                    {
                        var subpages = System.IO.Path.Combine(virtualFolder, title);
                        plan.Moves.Add(new PathMove(System.IO.Path.Combine(virtualFolder, child.Name), subpages));
                        page.Subpages = zipFolder + "/" + title;
                        Number(child, subpages, page.Subpages, page, plan);
                    }

                    plan.Pages.Add((child.Path, target, page.Path, page));
                    item.Children.Add(page);
                    break;
                }

                default:
                {
                    var name = prefix + child.Name;
                    plan.Moves.Add(new PathMove(System.IO.Path.Combine(virtualFolder, child.EntryName), System.IO.Path.Combine(virtualFolder, name)));
                    var file = new ExportItem { Kind = "file", Name = child.Name, Path = zipFolder + "/" + name, Source = source, ModifiedUtc = File.GetLastWriteTimeUtc(child.Path) };
                    plan.Files.Add((child.Path, file.Path));
                    item.Children.Add(file);
                    break;
                }
            }
        }
    }

    private static void WritePage(ZipArchive zip, (string Source, string Target, string ZipPath, ExportItem Item) page, ExportPlan plan)
    {
        var loaded = TextDocumentFile.Load(page.Source);
        var (text, _) = LinkUpdater.Rewrite(loaded.Text, page.Target, plan.Moves, onlyInside: plan.SourceRoot);
        var pages = plan.Titles.Keys.ToList();
        (text, _) = WikiLinkRenamer.Rename(text, target =>
            WikiLinkResolver.Resolve(target, page.Source, pages, plan.SourceRoot) is { } resolved && plan.Titles.TryGetValue(resolved, out var numbered)
                ? numbered
                : null,
            keepOldTitleAsLabel: true);

        var tags = PageFacts.Extract(loaded.Text).Tags.Select(t => t.Tag).Distinct().ToList();
        page.Item.Tags = tags.Count > 0 ? tags : null;

        var entry = zip.CreateEntry(page.ZipPath, CompressionLevel.Optimal);
        entry.LastWriteTime = File.GetLastWriteTime(page.Source);
        using var stream = entry.Open();
        stream.Write(loaded.Format.Encode(text));
    }

    private static void AddFolder(ZipArchive zip, string source, string zipFolder)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            zip.CreateEntryFromFile(file, zipFolder + "/" + Relative(source, file), CompressionLevel.Optimal);
        }
    }

    internal static string Relative(string root, string path) =>
        System.IO.Path.GetRelativePath(root, path).Replace(System.IO.Path.DirectorySeparatorChar, '/');

    /// <summary>A display name made safe as a file or folder name.</summary>
    public static string SafeName(string name)
    {
        var cleaned = new string(name.Select(c => c < 32 || "\\/:*?\"<>|".Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "Notebook" : cleaned;
    }
}

/// <summary>
/// Imports an export zip as a new notebook folder: original names, order and colors come back from
/// <c>ultiviewer-export.json</c>; without it, numeric prefixes ("01 ", "02_", "3-") are stripped and used as
/// the order. Links between pages are rewritten to the restored names.
/// </summary>
public static partial class NotebookImporter
{
    /// <summary>Unpacks <paramref name="zipPath"/> into a new folder under <paramref name="destinationParent"/> and returns it.</summary>
    public static string Import(string zipPath, string destinationParent)
    {
        Directory.CreateDirectory(destinationParent);
        var staging = System.IO.Path.Combine(destinationParent, ".uv-import-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var manifest = Extract(zipPath, staging);
            var top = manifest is not null ? System.IO.Path.Combine(staging, manifest.Root.Path) : SingleTopFolder(staging);
            if (!Directory.Exists(top))
            {
                throw new InvalidDataException("The archive doesn't contain the folder its manifest describes.");
            }

            var name = NotebookExporter.SafeName(manifest?.Name ?? (top == staging ? System.IO.Path.GetFileNameWithoutExtension(zipPath) : System.IO.Path.GetFileName(top)));
            var moves = new List<PathMove>();
            var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (manifest is not null)
            {
                Restore(manifest.Root, top, moves, titles);
            }
            else
            {
                StripPrefixes(top, moves, titles);
            }

            foreach (var page in NotebookScanner.EnumeratePageFiles(top))
            {
                var loaded = TextDocumentFile.Load(page);
                var (text, changed) = LinkUpdater.Rewrite(loaded.Text, page, moves, onlyInside: top);
                var (renamed, wikiChanged) = WikiLinkRenamer.Rename(text, t => titles.GetValueOrDefault(t), keepOldTitleAsLabel: false);
                var final = WikiLinkRenamer.DropRedundantLabels(renamed);
                if (changed > 0 || wikiChanged > 0 || final != renamed)
                {
                    TextDocumentFile.Save(page, final, loaded.Format);
                }
            }

            var destination = System.IO.Path.Combine(destinationParent, NotebookOperations.UniqueName(destinationParent, name));
            Directory.Move(top, destination);
            return destination;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    /// <summary>Extracts every entry under <paramref name="staging"/> (refusing paths that escape it) and returns the manifest, if any.</summary>
    private static ExportManifest? Extract(string zipPath, string staging)
    {
        ExportManifest? manifest = null;
        var root = System.IO.Path.GetFullPath(staging) + System.IO.Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName == ExportManifest.FileName)
            {
                using var json = entry.Open();
                manifest = JsonSerializer.Deserialize<ExportManifest>(json, JsonFileStore.Options);
                if (manifest?.Format != ExportManifest.FormatName)
                {
                    manifest = null; // someone else's JSON: fall back to prefixes
                }

                continue;
            }

            var target = System.IO.Path.GetFullPath(System.IO.Path.Combine(staging, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The archive contains an unsafe path: {entry.FullName}");
            }

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target);
        }

        return manifest;
    }

    private static string SingleTopFolder(string staging)
    {
        var folders = Directory.GetDirectories(staging);
        var files = Directory.GetFiles(staging);
        return folders.Length == 1 && files.Length == 0 ? folders[0] : staging;
    }

    /// <summary>Renames items back to their original names (top-down) and writes order/color sidecars.</summary>
    private static void Restore(ExportItem item, string folder, List<PathMove> moves, Dictionary<string, string> titles)
    {
        var order = new List<string>();
        foreach (var child in item.Children ?? [])
        {
            var current = System.IO.Path.Combine(folder, LastSegment(child.Path));
            switch (child.Kind)
            {
                case "page":
                {
                    var numbered = System.IO.Path.GetFileNameWithoutExtension(current);
                    var title = Rename(current, System.IO.Path.Combine(folder, child.Name + NotebookScanner.PageExtension), moves) ? child.Name : numbered;
                    titles[numbered] = title;
                    order.Add(title + NotebookScanner.PageExtension);
                    if (child.Attachments is { } attachments)
                    {
                        Rename(System.IO.Path.Combine(folder, LastSegment(attachments)), System.IO.Path.Combine(folder, title + NotebookScanner.AssetsSuffix), moves);
                    }

                    if (child.Subpages is { } subpages)
                    {
                        var subfolder = System.IO.Path.Combine(folder, title);
                        Rename(System.IO.Path.Combine(folder, LastSegment(subpages)), subfolder, moves);
                        Restore(child, subfolder, moves, titles);
                    }

                    break;
                }

                case "section":
                {
                    var restored = System.IO.Path.Combine(folder, child.Name);
                    var target = Rename(current, restored, moves) ? restored : current;
                    order.Add(System.IO.Path.GetFileName(target));
                    Restore(child, target, moves, titles);
                    break;
                }

                default:
                {
                    var restored = System.IO.Path.Combine(folder, child.Name);
                    order.Add(System.IO.Path.GetFileName(Rename(current, restored, moves) ? restored : current));
                    break;
                }
            }
        }

        FolderMetadataStore.Write(folder, new FolderMetadata { Order = order, Color = item.Color });
    }

    /// <summary>Without a manifest: strip "NN " prefixes where every entry of a folder has one, keeping their order.</summary>
    private static void StripPrefixes(string folder, List<PathMove> moves, Dictionary<string, string> titles)
    {
        var entries = Directory.EnumerateFileSystemEntries(folder)
            .Select(System.IO.Path.GetFileName)
            .OfType<string>()
            .Where(n => !NotebookScanner.IsHiddenName(n))
            .ToList();
        var parsed = entries.Select(n => (Name: n, Match: Prefix().Match(n))).ToList();
        if (parsed.Count > 0 && parsed.All(p => p.Match.Success))
        {
            var order = new List<string>();
            foreach (var (name, match) in parsed.OrderBy(p => long.Parse(p.Match.Groups["number"].Value)).ThenBy(p => p.Name, StringComparer.Ordinal))
            {
                var stripped = match.Groups["rest"].Value;
                var current = System.IO.Path.Combine(folder, name);
                var final = Rename(current, System.IO.Path.Combine(folder, stripped), moves) ? stripped : name;
                if (NotebookScanner.IsPageFile(name))
                {
                    titles[System.IO.Path.GetFileNameWithoutExtension(name)] = System.IO.Path.GetFileNameWithoutExtension(final);
                }

                var isPageCompanion = Directory.Exists(System.IO.Path.Combine(folder, final))
                    && (final.EndsWith(NotebookScanner.AssetsSuffix, StringComparison.OrdinalIgnoreCase)
                        || parsed.Any(p => p.Match.Groups["rest"].Value.Equals(final + NotebookScanner.PageExtension, StringComparison.OrdinalIgnoreCase)));
                if (!isPageCompanion)
                {
                    order.Add(final);
                }
            }

            FolderMetadataStore.Write(folder, new FolderMetadata { Order = order });
        }

        foreach (var sub in Directory.GetDirectories(folder).Where(d => !NotebookScanner.IsHiddenName(System.IO.Path.GetFileName(d))))
        {
            StripPrefixes(sub, moves, titles);
        }
    }

    /// <summary>Renames a file or folder unless the name is invalid or taken; records the move.</summary>
    private static bool Rename(string current, string target, List<PathMove> moves)
    {
        if (string.Equals(current, target, StringComparison.Ordinal))
        {
            return true;
        }

        var name = System.IO.Path.GetFileName(target);
        var stem = name.EndsWith(NotebookScanner.AssetsSuffix, StringComparison.OrdinalIgnoreCase) ? name[..^NotebookScanner.AssetsSuffix.Length] : name;
        if (FileNameRules.Validate(NotebookScanner.IsPageFile(stem) ? System.IO.Path.GetFileNameWithoutExtension(stem) : stem) is not null
            || File.Exists(target) || Directory.Exists(target)
            || !(File.Exists(current) || Directory.Exists(current)))
        {
            return false;
        }

        if (Directory.Exists(current))
        {
            Directory.Move(current, target);
        }
        else
        {
            File.Move(current, target);
        }

        moves.Add(new PathMove(current, target));
        return true;
    }

    private static string LastSegment(string zipPath) => zipPath.TrimEnd('/').Split('/')[^1];

    [GeneratedRegex(@"^(?<number>\d{1,6})[ ._-]+(?<rest>\S.*)$")]
    private static partial Regex Prefix();
}
