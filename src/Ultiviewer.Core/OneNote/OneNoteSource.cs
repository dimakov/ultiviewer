using Ultiviewer.Core.OneNote.Store;

namespace Ultiviewer.Core.OneNote;

/// <summary>
/// A OneNote notebook opened for import: a <c>.onepkg</c> package (extracted to a temporary folder that
/// <see cref="Dispose"/> deletes), a notebook folder, its <c>.onetoc2</c>, or a single <c>.one</c> section.
/// Sections are listed in OneNote's order and read later, one at a time.
/// </summary>
public sealed class OneNoteSource : IDisposable
{
    public const string PackageExtension = ".onepkg";
    public const string SectionExtension = ".one";
    public const string TableOfContentsExtension = ".onetoc2";

    private const string RecycleBin = "OneNote_RecycleBin";
    private const int MaxDepth = 16;

    private readonly string? _temporaryFolder;

    private OneNoteSource(OneNoteNotebook notebook, string? temporaryFolder)
    {
        Notebook = notebook;
        _temporaryFolder = temporaryFolder;
    }

    public OneNoteNotebook Notebook { get; }

    /// <summary>True for the files and folders <see cref="Open"/> accepts.</summary>
    public static bool CanOpen(string path) =>
        Directory.Exists(path) || Path.GetExtension(path).ToLowerInvariant() is PackageExtension or SectionExtension or TableOfContentsExtension;

    /// <exception cref="OneNoteFormatException">The file isn't a OneNote notebook, package or section.</exception>
    public static OneNoteSource Open(string path)
    {
        if (Directory.Exists(path))
        {
            return new OneNoteSource(ReadNotebook(path, Path.GetFileName(Path.TrimEndingDirectorySeparator(path))), null);
        }

        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case SectionExtension:
                return new OneNoteSource(new OneNoteNotebook(Path.GetFileNameWithoutExtension(path),
                    [new OneNoteSectionFile(Path.GetFileNameWithoutExtension(path), path)]), null);
            case TableOfContentsExtension:
            {
                var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
                return new OneNoteSource(ReadNotebook(folder, Path.GetFileName(folder)), null);
            }

            case PackageExtension:
            {
                var temporary = Path.Combine(Path.GetTempPath(), "Ultiviewer", "onepkg-" + Guid.NewGuid().ToString("N")[..12]);
                try
                {
                    Cabinet.Extract(path, temporary);
                    var root = NotebookRoot(temporary);
                    return new OneNoteSource(ReadNotebook(root, Path.GetFileNameWithoutExtension(path)), temporary);
                }
                catch
                {
                    TryDelete(temporary);
                    throw;
                }
            }

            default:
                throw new OneNoteFormatException("Choose a OneNote notebook package (.onepkg), section (.one) or notebook folder.");
        }
    }

    /// <summary>Counts the sections of the notebook (in all section groups).</summary>
    public static int CountSections(IEnumerable<OneNoteEntry> entries) =>
        entries.Sum(e => e switch { OneNoteSectionFile => 1, OneNoteSectionGroup group => CountSections(group.Entries), _ => 0 });

    public void Dispose()
    {
        if (_temporaryFolder is not null)
        {
            TryDelete(_temporaryFolder);
        }
    }

    /// <summary>A notebook folder's sections and section groups: OneNote's order first, then the rest by name.</summary>
    public static OneNoteNotebook ReadNotebook(string folder, string name) => new(name, ReadEntries(folder, 0));

    private static List<OneNoteEntry> ReadEntries(string folder, int depth)
    {
        var entries = new List<(string FileName, OneNoteEntry Entry)>();
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            if (Path.GetExtension(file).Equals(SectionExtension, StringComparison.OrdinalIgnoreCase))
            {
                entries.Add((Path.GetFileName(file), new OneNoteSectionFile(Path.GetFileNameWithoutExtension(file), file)));
            }
        }

        if (depth < MaxDepth)
        {
            foreach (var sub in Directory.EnumerateDirectories(folder))
            {
                var subName = Path.GetFileName(sub);
                if (!subName.Equals(RecycleBin, StringComparison.OrdinalIgnoreCase) && ReadEntries(sub, depth + 1) is { Count: > 0 } children)
                {
                    entries.Add((subName, new OneNoteSectionGroup(subName, children)));
                }
            }
        }

        var order = Directory.EnumerateFiles(folder)
            .Where(f => Path.GetExtension(f).Equals(TableOfContentsExtension, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(OneNoteReader.ReadTableOfContents)
            .FirstOrDefault(toc => toc.Count > 0) ?? [];
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in order)
        {
            rank.TryAdd(entry, rank.Count);
        }

        return [.. entries
            .OrderBy(e => rank.GetValueOrDefault(e.FileName, int.MaxValue))
            .ThenBy(e => e.FileName, StringComparer.CurrentCultureIgnoreCase)
            .Select(e => e.Entry)];
    }

    /// <summary>The folder of an extracted package that holds the notebook: the one with the shallowest <c>.onetoc2</c>.</summary>
    private static string NotebookRoot(string extracted)
    {
        var toc = Directory.EnumerateFiles(extracted, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f).Equals(TableOfContentsExtension, StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetDirectoryName)
            .OrderBy(d => d!.Length)
            .FirstOrDefault();
        if (toc is not null)
        {
            return toc;
        }

        // No table of contents: a single top folder is the notebook.
        var folders = Directory.GetDirectories(extracted);
        return folders.Length == 1 && Directory.GetFiles(extracted).Length == 0 ? folders[0] : extracted;
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary folder is harmless.
        }
    }
}
