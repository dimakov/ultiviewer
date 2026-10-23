using System.Text;
using Ultiviewer.Core.Files;
using Ultiviewer.Core.Links;

namespace Ultiviewer.Core.Notebooks;

/// <summary>
/// Creates, renames, moves and deletes notebook items on disk and keeps the folder order sidecars in sync.
/// A page is up to three file-system entries: <c>Title.md</c>, the subpage folder <c>Title/</c> and the
/// attachment folder <c>Title.assets/</c>; they always move together.
/// </summary>
public sealed class NotebookOperations(IFileDeleter deleter)
{
    public static readonly Encoding NewFileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Creates <c>title.md</c> in <paramref name="container"/> and returns its path.</summary>
    /// <param name="insertAfterEntry">When the folder has a manual order, the new page goes after this entry.</param>
    public string CreatePage(string container, string title, string content = "", string? insertAfterEntry = null)
    {
        FileNameRules.EnsureValid(title);
        var entry = title + NotebookScanner.PageExtension;
        EnsureFree(container, entry);
        EnsureFree(container, title, "A section");
        Directory.CreateDirectory(container);
        var path = Path.Combine(container, entry);
        AtomicFile.WriteAllText(path, content, NewFileEncoding);
        InsertIntoOrder(container, entry, insertAfterEntry);
        return path;
    }

    /// <summary>Creates a section folder in <paramref name="container"/> and returns its path.</summary>
    public string CreateSection(string container, string name, string? insertAfterEntry = null)
    {
        FileNameRules.EnsureValid(name);
        EnsureFree(container, name);
        EnsureFree(container, name + NotebookScanner.PageExtension, "A page");
        var path = Path.Combine(container, name);
        Directory.CreateDirectory(path);
        InsertIntoOrder(container, name, insertAfterEntry);
        return path;
    }

    /// <summary>Returns <paramref name="baseName"/>, or "baseName 2", "baseName 3"… when a page or section has it.</summary>
    public static string UniqueName(string container, string baseName)
    {
        for (var n = 1; ; n++)
        {
            var candidate = n == 1 ? baseName : $"{baseName} {n}";
            if (!Exists(container, candidate) && !Exists(container, candidate + NotebookScanner.PageExtension))
            {
                return candidate;
            }
        }
    }

    /// <summary>Renames a section, page or file. Returns the new path.</summary>
    public string Rename(NotebookNode node, string newName)
    {
        FileNameRules.EnsureValid(newName);
        var parent = node.ParentFolder;
        switch (node.Kind)
        {
            case NodeKind.Section:
            {
                var target = Path.Combine(parent, newName);
                EnsureFreeUnlessSelf(target, node.Path);
                EnsureFree(parent, newName + NotebookScanner.PageExtension, "A page");
                Run(() => FileSystemMoves.MoveDirectory(node.Path, target));
                RenameInOrder(parent, node.EntryName, newName);
                return target;
            }

            case NodeKind.Page:
            {
                var target = Path.Combine(parent, newName + NotebookScanner.PageExtension);
                EnsureFreeUnlessSelf(target, node.Path);
                EnsureFreeUnlessSelf(Path.Combine(parent, newName), node.ChildFolder!);
                EnsureFreeUnlessSelf(Path.Combine(parent, newName + NotebookScanner.AssetsSuffix), node.AssetsFolder!);
                Run(() => MovePage(node, parent, newName));
                RewriteOwnAssetLinks(target, node.Name, newName);
                RenameInOrder(parent, node.EntryName, Path.GetFileName(target));
                return target;
            }

            case NodeKind.File:
            {
                var target = Path.Combine(parent, newName);
                EnsureFreeUnlessSelf(target, node.Path);
                Run(() => FileSystemMoves.MoveFile(node.Path, target));
                RenameInOrder(parent, node.EntryName, newName);
                return target;
            }

            default:
                throw new NotebookOperationException("Notebooks are renamed in the notebook list, not on disk.");
        }
    }

    /// <summary>
    /// Moves <paramref name="node"/> into <paramref name="targetContainer"/>. With an <paramref name="index"/>,
    /// the target folder gets a manual order with the node at that position; without one the node is appended
    /// (or sorted by name when the folder has no manual order). Returns the new path.
    /// </summary>
    public string Move(NotebookNode node, string targetContainer, int? index)
    {
        if (node.Kind == NodeKind.Notebook)
        {
            throw new NotebookOperationException("A notebook can't be moved into another notebook.");
        }

        if (FileSystemMoves.IsSameOrInside(targetContainer, node.Path)
            || (node.ChildFolder is { } children && FileSystemMoves.IsSameOrInside(targetContainer, children)))
        {
            throw new NotebookOperationException("An item can't be moved into itself.");
        }

        var source = node.ParentFolder;
        if (FileSystemMoves.IsSamePath(source, targetContainer))
        {
            if (index is { } position)
            {
                PlaceInOrder(targetContainer, node.EntryName, position);
            }

            return node.Path;
        }

        var newPath = Path.Combine(targetContainer, node.EntryName);
        EnsureFree(targetContainer, node.EntryName);
        if (node.Kind == NodeKind.Page)
        {
            EnsureFree(targetContainer, node.Name, "A section");
            EnsureFree(targetContainer, node.Name + NotebookScanner.AssetsSuffix, "An attachment folder");
        }
        else if (node.Kind == NodeKind.Section)
        {
            EnsureFree(targetContainer, node.Name + NotebookScanner.PageExtension, "A page");
        }

        Directory.CreateDirectory(targetContainer);
        Run(() =>
        {
            switch (node.Kind)
            {
                case NodeKind.Page:
                    MovePage(node, targetContainer, node.Name);
                    break;
                case NodeKind.Section:
                    FileSystemMoves.MoveDirectory(node.Path, newPath);
                    break;
                default:
                    FileSystemMoves.MoveFile(node.Path, newPath);
                    break;
            }
        });

        FolderMetadataStore.UpdateOrder(source, order => order.RemoveAll(n => SameName(n, node.EntryName)) > 0);
        if (index is { } targetIndex)
        {
            PlaceInOrder(targetContainer, node.EntryName, targetIndex);
        }
        else
        {
            InsertIntoOrder(targetContainer, node.EntryName, insertAfterEntry: null);
        }

        return newPath;
    }

    /// <summary>Deletes a section, page (with subpages and attachments) or file.</summary>
    public void Delete(NotebookNode node)
    {
        if (node.Kind == NodeKind.Notebook)
        {
            throw new NotebookOperationException("Close a notebook instead of deleting it.");
        }

        Run(() =>
        {
            foreach (var path in EntriesOf(node))
            {
                if (File.Exists(path) || Directory.Exists(path))
                {
                    deleter.Delete(path);
                }
            }
        });

        FolderMetadataStore.UpdateOrder(node.ParentFolder, order => order.RemoveAll(n => SameName(n, node.EntryName)) > 0);
    }

    /// <summary>Stores a manual order for <paramref name="container"/>.</summary>
    public static void SetOrder(string container, IReadOnlyList<string> entries) =>
        FolderMetadataStore.UpdateOrder(container, order =>
        {
            order.Clear();
            order.AddRange(entries);
            return true;
        });

    public static void SetColor(string folder, string? color)
    {
        var metadata = FolderMetadataStore.Read(folder);
        if (metadata.Color != color)
        {
            FolderMetadataStore.Write(folder, metadata with { Color = color });
        }
    }

    private static IEnumerable<string> EntriesOf(NotebookNode node)
    {
        yield return node.Path;
        if (node.Kind == NodeKind.Page)
        {
            yield return node.ChildFolder!;
            yield return node.AssetsFolder!;
        }
    }

    private static void MovePage(NotebookNode page, string targetFolder, string newTitle)
    {
        FileSystemMoves.MoveFile(page.Path, Path.Combine(targetFolder, newTitle + NotebookScanner.PageExtension));
        if (Directory.Exists(page.ChildFolder))
        {
            FileSystemMoves.MoveDirectory(page.ChildFolder!, Path.Combine(targetFolder, newTitle));
        }

        if (Directory.Exists(page.AssetsFolder))
        {
            FileSystemMoves.MoveDirectory(page.AssetsFolder!, Path.Combine(targetFolder, newTitle + NotebookScanner.AssetsSuffix));
        }
    }

    /// <summary>After a page rename, points the page's own links at the renamed attachment folder.</summary>
    private static void RewriteOwnAssetLinks(string pagePath, string oldTitle, string newTitle)
    {
        var bytes = File.ReadAllBytes(pagePath);
        var text = TextFileFormat.Decode(bytes, out var format);
        var oldPrefix = oldTitle + NotebookScanner.AssetsSuffix + "/";
        var rewritten = MarkdownLinks.Rewrite(text, (link, path) =>
        {
            if (link.Syntax == LinkSyntax.Wiki)
            {
                return null;
            }

            var relative = path.StartsWith("./", StringComparison.Ordinal) ? path[2..] : path;
            return relative.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase)
                ? newTitle + NotebookScanner.AssetsSuffix + "/" + relative[oldPrefix.Length..]
                : null;
        });

        if (!string.Equals(text, rewritten, StringComparison.Ordinal))
        {
            AtomicFile.WriteAllBytes(pagePath, format.Encode(rewritten));
        }
    }

    private static void InsertIntoOrder(string container, string entry, string? insertAfterEntry) =>
        FolderMetadataStore.UpdateOrder(container, order =>
        {
            if (order.Count == 0 || order.Any(n => SameName(n, entry)))
            {
                return false;
            }

            var after = insertAfterEntry is null ? -1 : order.FindIndex(n => SameName(n, insertAfterEntry));
            order.Insert(after >= 0 ? after + 1 : order.Count, entry);
            return true;
        });

    /// <summary>Gives the folder a manual order (from the current display order) with entry at index.</summary>
    private static void PlaceInOrder(string container, string entry, int index)
    {
        var entries = NotebookScanner.ListEntries(container);
        var current = entries.FindIndex(n => SameName(n, entry));
        if (current >= 0)
        {
            entries.RemoveAt(current);
            if (current < index)
            {
                index--;
            }
        }

        entries.Insert(Math.Clamp(index, 0, entries.Count), entry);
        SetOrder(container, entries);
    }

    private static void RenameInOrder(string container, string oldEntry, string newEntry) =>
        FolderMetadataStore.UpdateOrder(container, order =>
        {
            var index = order.FindIndex(n => SameName(n, oldEntry));
            if (index < 0)
            {
                return false;
            }

            order[index] = newEntry;
            return true;
        });

    private static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool Exists(string container, string entry)
    {
        var path = Path.Combine(container, entry);
        return File.Exists(path) || Directory.Exists(path);
    }

    private static void EnsureFree(string container, string entry, string what = "An item")
    {
        if (Exists(container, entry))
        {
            throw new NotebookOperationException($"{what} named \"{Path.GetFileNameWithoutExtension(entry)}\" already exists here.");
        }
    }

    private static void EnsureFreeUnlessSelf(string target, string self)
    {
        if ((File.Exists(target) || Directory.Exists(target)) && !FileSystemMoves.IsSamePath(target, self))
        {
            throw new NotebookOperationException($"\"{Path.GetFileName(target)}\" already exists here.");
        }
    }

    private static void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new NotebookOperationException(ex.Message, ex);
        }
    }
}
