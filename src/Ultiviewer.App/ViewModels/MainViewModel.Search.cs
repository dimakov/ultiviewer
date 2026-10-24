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

    public ObservableCollection<SearchScopeChoice> SearchScopes { get; } =
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
        SearchAsTable = Session.SearchAsTable;
        InitializeSavedSearches();
        foreach (var recent in Session.RecentSearches.Take(20))
        {
            RecentSearches.Add(recent);
        }

        _search = new SearchService(AppPaths.IndexDirectory) { IncludeAttachments = Settings.SearchAttachments };
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
            _dispatcher.BeginInvoke(() =>
            {
                IndexUpdated?.Invoke();
                RequestAttachmentIndexing();
            });
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

        if (AttachmentFiles.IsAttachment(path) || Directory.Exists(path))
        {
            RequestAttachmentIndexing();
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

        var buffers = OpenTabs.Where(t => t.Path is not null && t.IsDirty).ToDictionary(t => t.Path!, t => t.Document.Text, StringComparer.OrdinalIgnoreCase);
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

        UpdateTableColumns(text);
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
            SearchScopeOption.Fixed => SearchScope.Path,
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

    // ---- Replace in the listed pages ----

    [ObservableProperty]
    private bool _isReplaceInFilesOpen;

    [ObservableProperty]
    private string _replaceFind = "";

    [ObservableProperty]
    private string _replaceWith = "";

    [ObservableProperty]
    private bool _replaceMatchCase;

    [ObservableProperty]
    private bool _replaceUseRegex;

    partial void OnIsReplaceInFilesOpenChanged(bool value)
    {
        if (value && ReplaceFind.Length == 0)
        {
            ReplaceFind = SearchText.Trim().Trim('"');
        }
    }

    /// <summary>
    /// Replaces in every page listed in the results. Each changed page is opened in a tab and changed as one
    /// undoable edit, so it can be reviewed and reverted with Ctrl+Z (notebook pages then autosave).
    /// </summary>
    [RelayCommand]
    private void ReplaceInResults()
    {
        if (ReplaceFind.Length == 0 || SearchResults.Count == 0)
        {
            return;
        }

        var options = new Core.Text.FindOptions(ReplaceMatchCase, WholeWord: false, ReplaceUseRegex);
        System.Text.RegularExpressions.Regex regex;
        try
        {
            regex = Core.Text.TextSearcher.Build(ReplaceFind, options);
        }
        catch (ArgumentException ex)
        {
            _dialogs.ShowError($"The pattern is not a valid regular expression:\n{ex.Message}");
            return;
        }

        var plan = new List<(string Path, int Count)>();
        foreach (var page in SearchResults)
        {
            try
            {
                var text = OpenTabs.FirstOrDefault(t => t.IsAt(page.Path))?.Document.Text ?? Core.Files.TextDocumentFile.Load(page.Path).Text;
                var count = Core.Text.TextSearcher.FindAll(text, regex).Count;
                if (count > 0)
                {
                    plan.Add((page.Path, count));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ErrorLog.Write(ex, $"Replace preview for {page.Path}");
            }
        }

        if (plan.Count == 0)
        {
            _dialogs.ShowInfo($"\"{ReplaceFind}\" doesn't occur in the listed pages.");
            return;
        }

        var total = plan.Sum(p => p.Count);
        var details = string.Join("\n", plan.Take(12).Select(p => $"  {TitleOf(p.Path)} — {p.Count}")) + (plan.Count > 12 ? $"\n  … and {plan.Count - 12} more" : "");
        if (!_dialogs.Confirm(
                $"Replace {total} occurrence{(total == 1 ? "" : "s")} of \"{ReplaceFind}\" with \"{ReplaceWith}\" in {plan.Count} page{(plan.Count == 1 ? "" : "s")}?\n\n{details}\n\n"
                + "Each page opens in a tab; press Ctrl+Z there to undo its change."))
        {
            return;
        }

        var replaced = 0;
        foreach (var (path, _) in plan)
        {
            if (OpenDocument(path, preview: false, focus: false) is not { } tab)
            {
                continue;
            }

            var (text, count) = Core.Text.TextSearcher.ReplaceAll(tab.Document.Text, regex, ReplaceWith, options);
            if (count > 0)
            {
                tab.Document.Replace(0, tab.Document.TextLength, text);
                replaced += count;
            }
        }

        StatusMessage = $"Replaced {replaced} occurrence{(replaced == 1 ? "" : "s")} in {plan.Count} page{(plan.Count == 1 ? "" : "s")}";
        RunSearch();
    }

    /// <summary>Opens a result line and selects its first match.</summary>
    public void OpenSearchLine(SearchLineViewModel line, bool focusEditor)
    {
        if (line.IsAttachment)
        {
            OpenAttachment(line.Path, line.Line);
            return;
        }

        if (OpenDocument(line.Path, preview: false, line: line.Line, focus: focusEditor) is not null)
        {
            _dispatcher.BeginInvoke(() => SelectRangeRequested?.Invoke(line.Line, line.FirstColumn, line.FirstLength), DispatcherPriority.Loaded);
        }
    }

    public void OpenSearchPage(SearchPageViewModel page)
    {
        if (page.IsAttachment)
        {
            OpenAttachment(page.Path, page.Lines.FirstOrDefault()?.Line);
            return;
        }

        OpenDocument(page.Path, preview: false, line: page.Lines.FirstOrDefault()?.Line);
    }

    /// <summary>PDFs and pictures open in their default app (the match was on page <paramref name="page"/>).</summary>
    private void OpenAttachment(string path, int? page)
    {
        ShellService.Open(path);
        StatusMessage = page is { } number && Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            ? $"Opened {Path.GetFileName(path)}; the match is on page {number}"
            : $"Opened {Path.GetFileName(path)}";
    }

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
            .Concat(OpenTabs.Where(t => t.Path is not null).Select(t => t.Path!))
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
