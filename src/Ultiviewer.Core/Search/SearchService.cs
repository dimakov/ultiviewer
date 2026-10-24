using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Ultiviewer.Core.Files;
using Ultiviewer.Core.Notebooks;

namespace Ultiviewer.Core.Search;

public sealed record SearchHit(int Line, int Column, int Length, string LineText);

/// <param name="IsAttachment">A PDF or image: hits are per page (<see cref="SearchHit.Line"/> is the page number) and show a snippet.</param>
public sealed record PageResult(string Path, string Title, string? NotebookRoot, bool TitleMatched, IReadOnlyList<SearchHit> Hits, bool IsAttachment = false)
{
    /// <summary>The page's properties (front matter and <c>key:: value</c> fields), for the results table.</summary>
    public IReadOnlyList<PageProperty> Properties { get; init; } = [];

    /// <summary>Last write time of the file (local time); <see cref="DateTime.MinValue"/> if unknown.</summary>
    public DateTime Modified { get; init; }
}

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

    /// <summary>Whether searches include text extracted from PDFs and images.</summary>
    public bool IncludeAttachments { get; set; } = true;

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
            await foreach (var result in Confirm(candidates.Concat(delta), index, matcher, openBuffers, seen, cancellationToken))
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
            return index.Candidates(query, scope, includeAttachments: IncludeAttachments);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            FellBackToScan?.Invoke(index.Root, ex);
            return NotebookScanner.EnumeratePageFiles(index.Root).Where(p => scope is null || FileSystemMoves.IsSameOrInside(p, scope)).ToList();
        }
    }

    private static async IAsyncEnumerable<PageResult> Confirm(
        IEnumerable<string> paths,
        NotebookIndex? index,
        TextMatcher matcher,
        IReadOnlyDictionary<string, string> openBuffers,
        HashSet<string> seen,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var batch in paths.Where(seen.Add).Chunk(32))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var results = await Task.Run(
                () => batch.AsParallel().AsOrdered().WithCancellation(cancellationToken).Select(p => Evaluate(p, index, matcher, openBuffers)).ToList(),
                cancellationToken);
            foreach (var result in results.OfType<PageResult>())
            {
                yield return result;
            }
        }
    }

    private static PageResult? Evaluate(string path, NotebookIndex? index, TextMatcher matcher, IReadOnlyDictionary<string, string> openBuffers)
    {
        var root = index?.Root;
        if (index is not null && !NotebookScanner.IsPageFile(path) && AttachmentFiles.IsAttachment(path))
        {
            return EvaluateAttachment(path, index, matcher);
        }

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
        return new PageResult(path, title, root, matcher.TitleMatches(title), hits)
        {
            Properties = page.Facts.Properties,
            Modified = LastWrite(path),
        };
    }

    /// <summary>An attachment's stored text, one line per page, matched like a page; hits show a snippet of their page.</summary>
    private static PageResult? EvaluateAttachment(string path, NotebookIndex index, TextMatcher matcher)
    {
        if (index.AttachmentText(path) is not { } pages)
        {
            return null;
        }

        var title = Path.GetFileName(path);
        var content = new PageContent(title, string.Join('\n', pages));
        if (!matcher.Matches(content))
        {
            return null;
        }

        var hits = matcher.FindMatches(content).Select(m => Snippet(m.Line, m.Column, m.Length, content.Lines[m.Line - 1])).ToList();
        return new PageResult(path, title, index.Root, matcher.TitleMatches(title), hits, IsAttachment: true) { Modified = LastWrite(path) };
    }

    private static DateTime LastWrite(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTime(path) : DateTime.MinValue;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>Up to 70 characters on either side of a match, with ellipses where the page text was cut.</summary>
    internal static SearchHit Snippet(int line, int column, int length, string text)
    {
        const int context = 70;
        var start = Math.Max(0, column - context);
        var end = Math.Min(text.Length, column + length + context);
        var prefix = start > 0 ? "…" : "";
        var snippet = prefix + text[start..end] + (end < text.Length ? "…" : "");
        return new SearchHit(line, column - start + prefix.Length, length, snippet);
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
