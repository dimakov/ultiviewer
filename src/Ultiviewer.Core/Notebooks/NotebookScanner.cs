namespace Ultiviewer.Core.Notebooks;

/// <summary>
/// Builds a <see cref="NotebookNode"/> tree from a folder.
/// Folders are sections, <c>.md</c> files are pages, a page plus a folder with the same name is a
/// page with subpages, <c>*.assets</c> folders and dot-files are hidden, other files are listed as files.
/// </summary>
public static class NotebookScanner
{
    public const string PageExtension = ".md";
    public const string AssetsSuffix = ".assets";
    public const string TempSuffix = ".uvtmp";

    public static NotebookNode ScanNotebook(string root, string? displayName = null)
    {
        var metadata = FolderMetadataStore.Read(root);
        return new NotebookNode
        {
            Kind = NodeKind.Notebook,
            Name = string.IsNullOrWhiteSpace(displayName) ? FolderName(root) : displayName,
            Path = root,
            Color = metadata.Color,
            Children = ScanFolder(root, metadata, recursive: true),
        };
    }

    public static IReadOnlyList<NotebookNode> ScanFolder(string folder, bool recursive = true) =>
        ScanFolder(folder, FolderMetadataStore.Read(folder), recursive);

    /// <summary>Entry names of a folder in display order (one level, no recursion).</summary>
    public static List<string> ListEntries(string folder) =>
        ScanFolder(folder, recursive: false).Select(n => n.EntryName).ToList();

    public static bool IsPageFile(string fileName) =>
        fileName.EndsWith(PageExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>All page files of a notebook, applying the same hiding rules as the tree.</summary>
    public static IEnumerable<string> EnumeratePageFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            foreach (var info in EnumerateSafely(folder))
            {
                if (IsHidden(info))
                {
                    continue;
                }

                if (info is DirectoryInfo directory)
                {
                    if (!directory.Name.EndsWith(AssetsSuffix, StringComparison.OrdinalIgnoreCase)
                        && (directory.Attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        pending.Push(directory.FullName);
                    }
                }
                else if (IsPageFile(info.Name))
                {
                    yield return info.FullName;
                }
            }
        }
    }

    public static bool IsHiddenName(string name) =>
        name.StartsWith('.') || name.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase);

    public static string FolderName(string folder) =>
        System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(folder)) is { Length: > 0 } name
            ? name
            : folder;

    private static IReadOnlyList<NotebookNode> ScanFolder(string folder, FolderMetadata metadata, bool recursive)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var directories = new Dictionary<string, DirectoryInfo>(StringComparer.OrdinalIgnoreCase);
        var files = new List<FileInfo>();
        foreach (var info in EnumerateSafely(folder))
        {
            if (IsHidden(info))
            {
                continue;
            }

            if (info is DirectoryInfo directory)
            {
                if (!directory.Name.EndsWith(AssetsSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    directories[directory.Name] = directory;
                }
            }
            else if (info is FileInfo file)
            {
                files.Add(file);
            }
        }

        var nodes = new List<NotebookNode>(directories.Count + files.Count);
        foreach (var file in files)
        {
            if (IsPageFile(file.Name))
            {
                var title = System.IO.Path.GetFileNameWithoutExtension(file.Name);
                IReadOnlyList<NotebookNode> children = [];
                if (directories.Remove(title, out var childDirectory) && recursive)
                {
                    children = ScanFolder(childDirectory.FullName, recursive: true);
                }

                nodes.Add(new NotebookNode { Kind = NodeKind.Page, Name = title, Path = file.FullName, Children = children });
            }
            else
            {
                nodes.Add(new NotebookNode { Kind = NodeKind.File, Name = file.Name, Path = file.FullName });
            }
        }

        foreach (var directory in directories.Values)
        {
            var childMetadata = FolderMetadataStore.Read(directory.FullName);
            nodes.Add(new NotebookNode
            {
                Kind = NodeKind.Section,
                Name = directory.Name,
                Path = directory.FullName,
                Color = childMetadata.Color,
                Children = recursive ? ScanFolder(directory.FullName, childMetadata, recursive: true) : [],
            });
        }

        return OrderMerger.Apply(nodes, n => n.EntryName, metadata.Order, DefaultOrder);
    }

    /// <summary>Without a stored order, sections come first, then pages and files, each by natural name.</summary>
    private static readonly IComparer<NotebookNode> DefaultOrder = Comparer<NotebookNode>.Create((a, b) =>
    {
        var byKind = (a.Kind == NodeKind.Section ? 0 : 1).CompareTo(b.Kind == NodeKind.Section ? 0 : 1);
        return byKind != 0 ? byKind : NaturalStringComparer.Instance.Compare(a.Name, b.Name);
    });

    private static bool IsHidden(FileSystemInfo info) =>
        IsHiddenName(info.Name) || (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;

    private static IEnumerable<FileSystemInfo> EnumerateSafely(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).GetFileSystemInfos();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
