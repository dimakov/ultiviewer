namespace Ultiviewer.Core.Notebooks;

public enum NodeKind
{
    Notebook,
    Section,
    Page,
    File,
}

/// <summary>
/// Immutable snapshot of one item in a notebook folder tree.
/// </summary>
public sealed class NotebookNode
{
    public required NodeKind Kind { get; init; }

    /// <summary>Display name: folder name, page title (file name without .md) or file name.</summary>
    public required string Name { get; init; }

    /// <summary>Folder path for notebooks and sections, file path for pages and files.</summary>
    public required string Path { get; init; }

    /// <summary>Color from the folder sidecar (notebooks and sections only).</summary>
    public string? Color { get; init; }

    public IReadOnlyList<NotebookNode> Children { get; init; } = [];

    /// <summary>Name of this node inside its parent folder; the key used in order lists.</summary>
    public string EntryName => System.IO.Path.GetFileName(Path);

    public string ParentFolder => System.IO.Path.GetDirectoryName(Path) ?? Path;

    /// <summary>
    /// Folder that holds this node's children: the folder itself for notebooks and sections,
    /// the sibling folder named after the title for pages (it may not exist yet), none for files.
    /// </summary>
    public string? ChildFolder => Kind switch
    {
        NodeKind.Notebook or NodeKind.Section => Path,
        NodeKind.Page => System.IO.Path.Combine(ParentFolder, Name),
        _ => null,
    };

    /// <summary>Folder for pasted images and attachments of a page.</summary>
    public string? AssetsFolder => Kind == NodeKind.Page
        ? System.IO.Path.Combine(ParentFolder, Name + NotebookScanner.AssetsSuffix)
        : null;

    public bool CanHaveChildren => Kind != NodeKind.File;

    public IEnumerable<NotebookNode> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var node in child.DescendantsAndSelf())
            {
                yield return node;
            }
        }
    }
}
