using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ultiviewer.App.Services;
using Ultiviewer.Core.Notebooks;
using Ultiviewer.Core.Search;
using Ultiviewer.Core.Settings;

namespace Ultiviewer.App.ViewModels;

/// <summary>
/// Notebook search (Ctrl+Shift+F), keeping the per-notebook indexes current, and the Ctrl+E page switcher.
/// </summary>
public sealed partial class MainViewModel
{
    private const int MaxResultPages = 500;
    private const int MaxRecentPages = 40;

    private SearchService? _search;
    private CancellationTokenSource? _searchCancellation;
    private DispatcherTimer? _searchDebounce;

    public ObservableCollection<SearchPageViewModel> SearchResults { get; } = [];

    public ObservableCollection<string> RecentSearches { get; } = [];

    public IReadOnlyList<SearchScopeChoice> SearchScopes { get; } =
    [
        new(SearchScopeOption.AllNotebooks, "All notebooks"),
        new(SearchScopeOption.ThisNotebook, "This notebook"),
        new(SearchScopeOption.ThisSection, "This section"),
        new(SearchScopeOption.ThisPage, "This page"),
    ];

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private bool _isSearchPanelOpen;

    [ObservableProperty]
    private string _searchStatus = "";

    [ObservableProperty]
    private SearchScopeChoice? _searchScope;

    /// <summary>Asks the view to focus (and select) the search box.</summary>
    public event Action? SearchFocusRequested;

    /// <summary>Asks the view to select a range in the active editor: line (1-based), column, length.</summary>
    public event Action<int, int, int>? SelectRangeRequested;

    public SearchService? Search => _search;

    private void InitializeSearch()
    {
        SearchScope = SearchScopes[0];
        foreach (var recent in Session.RecentSearches.Take(20))
        {
            RecentSearches.Add(recent);
        }

        _search = new SearchService(AppPaths.IndexDirectory);
        _search.FellBackToScan += (root, ex) => ErrorLog.Write(ex, $"Search index unavailable for {root}; scanned files instead");
        _searchDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            _searchDebounce!.Stop();
            RunSearch();
        }, _dispatcher);
        _searchDebounce.Stop();
        SyncSearchNotebooks();
        Notebooks.CollectionChanged += (_, _) => SyncSearchNotebooks();
    }

    private void SyncSearchNotebooks()
    {
        if (_search is not { } search)
        {
            return;
        }

        var roots = Notebooks.Select(n => n.Path).ToList();
        _ = Task.Run(() =>
        {
            search.SetNotebooks(roots);
            search.UpdateAll();
            _dispatcher.BeginInvoke(() => IndexUpdated?.Invoke());
        });
    }

    /// <summary>Raised on the UI thread after the indexes changed (tags and to-do panes refresh from it).</summary>
    public event Action? IndexUpdated;

    partial void OnDocumentSaved(DocumentTabViewModel tab)
    {
        if (_search is { } search && tab.Path is { } path && NotebookOf(path) is not null)
        {
            _ = Task.Run(() =>
            {
                search.Refresh(path);
                _dispatcher.BeginInvoke(() => IndexUpdated?.Invoke());
            });
        }
    }

    partial void OnNotebookFileChanged(string path)
    {
        if (_search is not { } search)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            if (NotebookScanner.IsPageFile(path) && !Directory.Exists(path))
            {
                search.Refresh(path);
            }
            else
            {
                // A folder was renamed, moved or deleted: re-check the whole notebook (cheap when little changed).
                search.IndexFor(path)?.Update();
            }

            _dispatcher.BeginInvoke(() => IndexUpdated?.Invoke());
        });
    }

    partial void OnNotebookRescanRequested(TreeNodeViewModel root)
    {
        if (_search is { } search)
        {
            _ = Task.Run(() => search.UpdateAll());
        }
    }

    [RelayCommand]
    private void OpenSearch(string? initialText)
    {
        IsSearchPanelOpen = true;
        if (!string.IsNullOrWhiteSpace(initialText) && !initialText.Contains('\n'))
        {
            SearchText = initialText.Trim();
        }

        SearchFocusRequested?.Invoke();
    }

    [RelayCommand]
    private void CloseSearch()
    {
        IsSearchPanelOpen = false;
        _searchCancellation?.Cancel();
        EditorFocusRequested?.Invoke();
    }

    /// <summary>Searches for a tag (from the tags pane or a clicked tag).</summary>
    public void SearchFor(string text)
    {
        IsSearchPanelOpen = true;
        SearchScope = SearchScopes[0];
        SearchText = text;
        RunSearch();
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchDebounce?.Stop();
        _searchDebounce?.Start();
    }

    partial void OnSearchScopeChanged(SearchScopeChoice? value) => RunSearch();

    public async void RunSearch()
    {
        _searchDebounce?.Stop();
        _searchCancellation?.Cancel();
        var cancellation = _searchCancellation = new CancellationTokenSource();
        SearchResults.Clear();
        var text = SearchText.Trim();
        if (_search is null || text.Length == 0 || !IsSearchPanelOpen)
        {
            SearchStatus = "";
            return;
        }

        var buffers = Tabs.Where(t => t.Path is not null && t.IsDirty).ToDictionary(t => t.Path!, t => t.Document.Text, StringComparer.OrdinalIgnoreCase);
        var scope = new SearchScope(ScopePath());
        var stopwatch = Stopwatch.StartNew();
        var pages = 0;
        var matches = 0;
        SearchStatus = "Searching…";
        try
        {
            await foreach (var result in _search.SearchAsync(text, scope, buffers, cancellation.Token))
            {
                if (cancellation.IsCancellationRequested)
                {
                    return;
                }

                var page = new SearchPageViewModel(result, LocationOf(result.Path));
                SearchResults.Add(page);
                pages++;
                matches += page.MatchCount;
                SearchStatus = $"{pages} pages, {matches} matches…";
                if (pages >= MaxResultPages)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cancellation.IsCancellationRequested)
        {
            return;
        }

        SearchStatus = pages == 0
            ? $"No matches · {stopwatch.ElapsedMilliseconds} ms"
            : $"{pages} page{(pages == 1 ? "" : "s")}, {matches} match{(matches == 1 ? "" : "es")} · {stopwatch.ElapsedMilliseconds} ms";
        if (pages > 0)
        {
            RememberSearch(text);
        }
    }

    private string? ScopePath()
    {
        var context = ActiveTab?.Path ?? SelectedNode?.Path;
        return SearchScope?.Value switch
        {
            SearchScopeOption.ThisNotebook when context is not null => NotebookOf(context)?.Path,
            SearchScopeOption.ThisSection when context is not null => File.Exists(context) ? Path.GetDirectoryName(context) : context,
            SearchScopeOption.ThisPage when ActiveTab?.Path is { } page => page,
            _ => null,
        };
    }

    private string LocationOf(string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        if (NotebookOf(path) is not { } notebook)
        {
            return folder;
        }

        var relative = Path.GetRelativePath(notebook.Path, folder);
        return relative == "." ? notebook.Name : notebook.Name + " › " + relative.Replace(Path.DirectorySeparatorChar.ToString(), " › ");
    }

    private void RememberSearch(string text)
    {
        RecentSearches.Remove(text);
        RecentSearches.Insert(0, text);
        while (RecentSearches.Count > 20)
        {
            RecentSearches.RemoveAt(RecentSearches.Count - 1);
        }

        Session.RecentSearches = RecentSearches.ToList();
    }

    /// <summary>Opens a result line and selects its first match.</summary>
    public void OpenSearchLine(SearchLineViewModel line, bool focusEditor)
    {
        if (OpenDocument(line.Path, preview: false, line: line.Line, focus: focusEditor) is not null)
        {
            _dispatcher.BeginInvoke(() => SelectRangeRequested?.Invoke(line.Line, line.FirstColumn, line.FirstLength), DispatcherPriority.Loaded);
        }
    }

    public void OpenSearchPage(SearchPageViewModel page) =>
        OpenDocument(page.Path, preview: false, line: page.Lines.FirstOrDefault()?.Line);

    // ---- Recent pages and the Ctrl+E switcher ----

    partial void RememberRecent(string path)
    {
        Session.RecentPages.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        Session.RecentPages.Insert(0, path);
        if (Session.RecentPages.Count > MaxRecentPages)
        {
            Session.RecentPages.RemoveRange(MaxRecentPages, Session.RecentPages.Count - MaxRecentPages);
        }
    }

    /// <summary>Pages for the switcher: recent first when the query is empty, otherwise fuzzy-ranked titles.</summary>
    public IReadOnlyList<QuickOpenItem> QuickOpenItems(string query)
    {
        var pages = AllPagePaths()
            .Concat(Tabs.Where(t => t.Path is not null).Select(t => t.Path!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (string.IsNullOrWhiteSpace(query))
        {
            var recent = Session.RecentPages.Where(File.Exists).ToList();
            return recent.Concat(pages.Except(recent, StringComparer.OrdinalIgnoreCase))
                .Take(50)
                .Select(p => new QuickOpenItem(p, TitleOf(p), LocationOf(p), 0))
                .ToList();
        }

        var recentRank = Session.RecentPages.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i, StringComparer.OrdinalIgnoreCase);
        // The location only counts for multi-part queries ("proj budget", "cooking/bread"); single words match titles.
        var usesLocation = query.Trim().IndexOfAny([' ', '/', '\\', '>']) > 0;
        return pages
            .Select(p =>
            {
                var title = TitleOf(p);
                var score = FuzzyMatcher.Score(query, title)
                    ?? (usesLocation && FuzzyMatcher.Score(query, LocationOf(p) + " " + title) is { } pathScore ? pathScore / 2 : null);
                if (score is { } s && recentRank.TryGetValue(p, out var rank))
                {
                    score = s + Math.Max(0, 20 - rank);
                }

                return score is null ? null : new QuickOpenItem(p, title, LocationOf(p), score.Value);
            })
            .OfType<QuickOpenItem>()
            .OrderByDescending(i => i.Score)
            .ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(50)
            .ToList();
    }

    private static string TitleOf(string path) =>
        NotebookScanner.IsPageFile(path) ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(path);

    /// <summary>Raised by Ctrl+E; the view shows the switcher.</summary>
    public event Action? QuickOpenRequested;

    [RelayCommand]
    private void QuickOpen() => QuickOpenRequested?.Invoke();
}
