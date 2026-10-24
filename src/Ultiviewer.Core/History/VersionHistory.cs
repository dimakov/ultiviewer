using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ultiviewer.Core.Files;
using Ultiviewer.Core.Settings;

namespace Ultiviewer.Core.History;

public enum VersionReason
{
    /// <summary>The file as it was on disk before this session's first save.</summary>
    BeforeEditing,

    /// <summary>Saved from the editor (autosave or Ctrl+S), at most once per interval.</summary>
    Saved,

    /// <summary>Another program changed the file.</summary>
    ChangedOnDisk,

    /// <summary>The text last saved here, just before another program replaced it.</summary>
    BeforeOutsideChange,

    /// <summary>Unsaved text kept when the file changed on disk at the same time (a conflict).</summary>
    Conflict,

    /// <summary>The text that was replaced by restoring an older version.</summary>
    BeforeRestore,
}

/// <summary>One stored version of a file.</summary>
public sealed record VersionInfo(string Id, DateTime TimeUtc, int Length, string Hash, VersionReason Reason);

/// <summary>
/// Per-user version history of text files: <c>history/&lt;key&gt;/index.json</c> plus one gzipped UTF-8 text per
/// version, where the key is a hash of the file's full path. Identical consecutive versions are not stored,
/// saves are thinned to one per <see cref="Interval"/>, and old versions are pruned by age (<see cref="Prune"/>).
/// </summary>
public sealed class VersionHistory(string root)
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public string Root { get; } = root;

    /// <summary>Saves closer together than this keep only the first of them.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

    public static string KeyFor(string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())))[..20];

    /// <summary>Versions of <paramref name="path"/>, newest first.</summary>
    public IReadOnlyList<VersionInfo> List(string path) => Load(Folder(path)) is { } index ? NewestFirst(index.Versions).ToList() : [];

    /// <summary>Versions recorded in the same millisecond get ids with an "x" suffix, so the id breaks ties.</summary>
    private static IOrderedEnumerable<VersionInfo> NewestFirst(IEnumerable<VersionInfo> versions) =>
        versions.OrderByDescending(v => v.TimeUtc).ThenByDescending(v => v.Id, StringComparer.Ordinal);

    public string? Read(string path, string id)
    {
        var file = Path.Combine(Folder(path), id + ".txt.gz");
        try
        {
            using var stream = new GZipStream(File.OpenRead(file), CompressionMode.Decompress);
            using var reader = new StreamReader(stream, Utf8);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Stores <paramref name="text"/> as a version of <paramref name="path"/> unless it equals the newest version,
    /// or it is a save within <see cref="Interval"/> of the previous save. Returns the new version, if any.
    /// </summary>
    public VersionInfo? Record(string path, string text, VersionReason reason, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var folder = Folder(path);
        var index = Load(folder) ?? new HistoryIndex { Path = Path.GetFullPath(path) };
        var hash = TextDocumentFile.Hash(Utf8.GetBytes(text));
        var newest = NewestFirst(index.Versions).FirstOrDefault();
        if (newest is not null
            && (newest.Hash == hash || (reason == VersionReason.Saved && newest.Reason == VersionReason.Saved && now - newest.TimeUtc < Interval)))
        {
            return null;
        }

        var id = now.ToString("yyyyMMdd'T'HHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        while (index.Versions.Any(v => v.Id == id))
        {
            id += "x";
        }

        var version = new VersionInfo(id, now, text.Length, hash, reason);
        try
        {
            Directory.CreateDirectory(folder);
            using (var stream = new GZipStream(File.Create(Path.Combine(folder, id + ".txt.gz")), CompressionLevel.Optimal))
            {
                stream.Write(Utf8.GetBytes(text));
            }

            index.Versions.Add(version);
            var keep = Prune(index.Versions, now);
            foreach (var dropped in index.Versions.Except(keep).ToList())
            {
                TryDelete(Path.Combine(folder, dropped.Id + ".txt.gz"));
            }

            index.Versions = [.. keep];
            index.Path = Path.GetFullPath(path);
            Save(folder, index);
            return version;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // history is best effort; saving the file itself already succeeded
        }
    }

    /// <summary>Moves the history of a file, or of every file under a folder, after a rename or move.</summary>
    public void Move(string oldPath, string newPath)
    {
        if (!Directory.Exists(Root))
        {
            return;
        }

        var oldFull = Path.GetFullPath(oldPath);
        var newFull = Path.GetFullPath(newPath);
        foreach (var folder in Directory.EnumerateDirectories(Root))
        {
            if (Load(folder) is not { } index || !FileSystemMoves.IsSameOrInside(index.Path, oldFull))
            {
                continue;
            }

            var movedPath = string.Equals(index.Path, oldFull, StringComparison.OrdinalIgnoreCase)
                ? newFull
                : Path.Combine(newFull, Path.GetRelativePath(oldFull, index.Path));
            var target = Folder(movedPath);
            try
            {
                if (Directory.Exists(target) && Load(target) is { } existing)
                {
                    // Something already has history at the new path: merge the two timelines.
                    foreach (var version in index.Versions.Where(v => existing.Versions.All(e => e.Id != v.Id)))
                    {
                        File.Move(Path.Combine(folder, version.Id + ".txt.gz"), Path.Combine(target, version.Id + ".txt.gz"));
                        existing.Versions.Add(version);
                    }

                    existing.Path = movedPath;
                    Save(target, existing);
                    Directory.Delete(folder, recursive: true);
                }
                else
                {
                    Directory.Move(folder, target);
                    index.Path = movedPath;
                    Save(target, index);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// The versions to keep: everything from the last day, the newest per hour for 30 days, the newest per day
    /// after that, and at most 500 in all.
    /// </summary>
    public static IReadOnlyList<VersionInfo> Prune(IEnumerable<VersionInfo> versions, DateTime nowUtc)
    {
        var keep = new List<VersionInfo>();
        var seenBuckets = new HashSet<string>();
        foreach (var version in NewestFirst(versions))
        {
            var age = nowUtc - version.TimeUtc;
            var bucket = age < TimeSpan.FromDays(1) ? version.Id
                : age < TimeSpan.FromDays(30) ? version.TimeUtc.ToString("yyyyMMddHH", System.Globalization.CultureInfo.InvariantCulture)
                : version.TimeUtc.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
            if (seenBuckets.Add(bucket))
            {
                keep.Add(version);
            }
        }

        return keep.Take(500).ToList();
    }

    private string Folder(string path) => Path.Combine(Root, KeyFor(path));

    private static HistoryIndex? Load(string folder) => File.Exists(Path.Combine(folder, "index.json"))
        ? JsonFileStore.Load<HistoryIndex>(Path.Combine(folder, "index.json"))
        : null;

    private static void Save(string folder, HistoryIndex index) => JsonFileStore.Save(Path.Combine(folder, "index.json"), index);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal sealed class HistoryIndex
    {
        public string Path { get; set; } = "";

        public List<VersionInfo> Versions { get; set; } = [];
    }
}
