using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Ultiviewer.Core.Files;
using Ultiviewer.Core.Notebooks;

namespace Ultiviewer.Core.Search;

public sealed record SearchHit(int Line, int Column, int Length, string LineText);

public sealed record PageResult(string Path, string Title, string? NotebookRoot, bool TitleMatched, IReadOnlyList<SearchHit> Hits);

/// <summary>Where to search: everywhere, one notebook, a folder (section) or a single page.</summary>
public sealed record SearchScope(string? Path)
{
    public static SearchScope Everywhere { get; } = new((string?)null);
}

/// <summary>
/// Search across notebooks, modelled on OneNote's search session:
/// <list type="number">
/// <item>bring each notebook's index up to date (cheap when nothing changed — OneNote's StartIndexingOnSearch),</item>
/// <item>ask the index for candidate pages,</item>
/// <item>add unsaved open buffers (the "delta" the index can't know about),</item>
/// <item>confirm every candidate on its real text and compute highlights (OneNote's grep step).</item>
/// </list>
/// If an index is unusable the notebook falls back to a full scan of its files.
/// </summary>
public sealed class SearchService(string indexFolder) : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, NotebookIndex> _indexes = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<NotebookIndex> Indexes
    {
        get
        {
            lock (_gate)
            {
                return _indexes.Values.ToList();
            }
        }
    }

    /// <summary>Raised (on a background thread) when an index could not be used and a full scan ran instead.</summary>
    public event Action<string, Exception>? FellBackToScan;

    /// <summary>Opens indexes for new notebooks and closes the ones no longer open.</summary>
    public void SetNotebooks(IEnumerable<string> roots)
    {
        var wanted = roots.Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            foreach (var root in _indexes.Keys.Where(k => !wanted.Contains(k)).ToList())
            {
                _indexes[root].Dispose();
                _indexes.Remove(root);
            }

            foreach (var root in wanted.Where(r => !_indexes.ContainsKey(r) && Directory.Exists(r)))
            {
                try
                {
                    _indexes[root] = NotebookIndex.Open(root, indexFolder);
                }
                catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
                {
                    FellBackToScan?.Invoke(root, ex);
                }
            }
        }
    }

    public NotebookIndex? IndexFor(string path)
    {
        lock (_gate)
        {
            return _indexes.Values.FirstOrDefault(i => FileSystemMoves.IsSameOrInside(path, i.Root));
        }
    }

    /// <summary>Re-indexes one changed file (saved by the app or changed on disk).</summary>
    public void Refresh(string path)
    {
        try
        {
            IndexFor(path)?.Refresh(path);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            FellBackToScan?.Invoke(path, ex);
        }
    }

    public void UpdateAll(CancellationToken cancellationToken = default)
    {
        foreach (var index in Indexes)
        {
            try
            {
                index.Update(cancellationToken);
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
            {
                FellBackToScan?.Invoke(index.Root, ex);
            }
        }
    }

    /// <summary>
    /// Streams matching pages. <paramref name="openBuffers"/> maps open documents' paths to their current
    /// (possibly unsaved) text, which wins over what is on disk.
    /// </summary>
    public async IAsyncEnumerable<PageResult> SearchAsync(
        string queryText,
        SearchScope scope,
        IReadOnlyDictionary<string, string> openBuffers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var query = SearchQuery.Parse(queryText);
        if (SearchQuery.IsEmpty(query))
        {
            yield break;
        }

        var matcher = new TextMatcher(query);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var indexes = Indexes.Where(i => scope.Path is null || FileSystemMoves.IsSameOrInside(scope.Path, i.Root)).ToList();

        foreach (var index in indexes)
        {
            var candidates = await Task.Run(() => CandidatesFor(index, query, scope.Path, cancellationToken), cancellationToken);
            var delta = openBuffers.Keys.Where(p => FileSystemMoves.IsSameOrInside(p, index.Root) && InScope(p, scope)).ToList();
            await foreach (var result in Confirm(candidates.Concat(delta), index.Root, matcher, openBuffers, seen, cancellationToken))
            {
                yield return result;
            }
        }

        if (scope.Path is null)
        {
            // Open files outside every notebook are searched too.
            var outside = openBuffers.Keys.Where(p => IndexFor(p) is null).ToList();
            await foreach (var result in Confirm(outside, null, matcher, openBuffers, seen, cancellationToken))
            {
                yield return result;
            }
        }
    }

    private IReadOnlyList<string> CandidatesFor(NotebookIndex index, AndNode query, string? scope, CancellationToken cancellationToken)
    {
        try
        {
            index.Update(cancellationToken);
            return index.Candidates(query, scope);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            FellBackToScan?.Invoke(index.Root, ex);
            return NotebookScanner.EnumeratePageFiles(index.Root).Where(p => scope is null || FileSystemMoves.IsSameOrInside(p, scope)).ToList();
        }
    }

    private static async IAsyncEnumerable<PageResult> Confirm(
        IEnumerable<string> paths,
        string? root,
        TextMatcher matcher,
        IReadOnlyDictionary<string, string> openBuffers,
        HashSet<string> seen,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var batch in paths.Where(seen.Add).Chunk(32))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var results = await Task.Run(
                () => batch.AsParallel().AsOrdered().WithCancellation(cancellationToken).Select(p => Evaluate(p, root, matcher, openBuffers)).ToList(),
                cancellationToken);
            foreach (var result in results.OfType<PageResult>())
            {
                yield return result;
            }
        }
    }

    private static PageResult? Evaluate(string path, string? root, TextMatcher matcher, IReadOnlyDictionary<string, string> openBuffers)
    {
        string text;
        if (!openBuffers.TryGetValue(path, out text!))
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                text = TextDocumentFile.Load(path).Text;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        var title = NotebookScanner.IsPageFile(path) ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(path);
        var page = new PageContent(title, text);
        if (!matcher.Matches(page))
        {
            return null;
        }

        var hits = matcher.FindMatches(page).Select(m => new SearchHit(m.Line, m.Column, m.Length, page.Lines[m.Line - 1])).ToList();
        return new PageResult(path, title, root, matcher.TitleMatches(title), hits);
    }

    private static bool InScope(string path, SearchScope scope) =>
        scope.Path is null || FileSystemMoves.IsSameOrInside(path, scope.Path);

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var index in _indexes.Values)
            {
                index.Dispose();
            }

            _indexes.Clear();
        }
    }
}
