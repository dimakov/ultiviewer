using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ultiviewer.Core.Notebooks;
using Ultiviewer.Core.Search;
using Ultiviewer.Core.Settings;

namespace Ultiviewer.App.ViewModels;

/// <summary>Saved searches (smart collections) and the property columns of the results table.</summary>
public sealed partial class MainViewModel
{
    private const int MaxTableColumns = 8;

    /// <summary>The searches listed above the notebook tree (the same objects as <c>Settings.SavedSearches</c>).</summary>
    public ObservableCollection<SavedSearch> SavedSearches { get; } = [];

    public bool HasSavedSearches => SavedSearches.Count > 0;

    /// <summary>Property keys shown as columns of the results table: the query's first, then the most common.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _tableColumns = [];

    [ObservableProperty]
    private bool _searchAsTable;

    [ObservableProperty]
    private bool _savedSearchesExpanded = true;

    partial void OnSearchAsTableChanged(bool value) => Session.SearchAsTable = value;

    partial void OnSavedSearchesExpandedChanged(bool value) => Session.SavedSearchesExpanded = value;

    private void InitializeSavedSearches()
    {
        SavedSearchesExpanded = Session.SavedSearchesExpanded;
        foreach (var saved in Settings.SavedSearches.Where(s => s.Query.Trim().Length > 0))
        {
            SavedSearches.Add(saved);
        }

        SavedSearches.CollectionChanged += (_, _) =>
        {
            Settings.SavedSearches = [.. SavedSearches];
            Settings.Save();
            OnPropertyChanged(nameof(HasSavedSearches));
        };
    }

    /// <summary>Saves the current query, scope and view under a name (an existing name is replaced).</summary>
    [RelayCommand]
    private void SaveSearch()
    {
        var query = SearchText.Trim();
        if (query.Length == 0)
        {
            StatusMessage = "Type a search first, then save it.";
            return;
        }

        var name = _dialogs.Prompt("Save search", "Name of the saved search:", query, n => n.Trim().Length == 0 ? "Enter a name." : null)?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        var saved = new SavedSearch { Name = name, Query = query, ScopePath = ScopePath(), ShowAsTable = SearchAsTable };
        var existing = SavedSearches.FirstOrDefault(s => s.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
        if (existing is not null)
        {
            SavedSearches[SavedSearches.IndexOf(existing)] = saved;
        }
        else
        {
            SavedSearches.Add(saved);
        }

        StatusMessage = $"Saved the search \"{name}\"";
    }

    [RelayCommand]
    private void RunSavedSearch(SavedSearch? saved)
    {
        if (saved is null)
        {
            return;
        }

        IsSearchPanelOpen = true;
        SearchAsTable = saved.ShowAsTable;
        SearchText = saved.Query;
        _searchDebounce?.Stop(); // the scope change below (or RunSearch) searches right away
        var scope = saved.ScopePath is { } path ? FixedScope(path) : SearchScopes[0];
        if (SearchScope == scope)
        {
            RunSearch();
        }
        else
        {
            SearchScope = scope;
        }
    }

    [RelayCommand]
    private void RenameSavedSearch(SavedSearch? saved)
    {
        if (saved is null || _dialogs.Prompt("Rename saved search", "Name:", saved.Name, n => n.Trim().Length == 0 ? "Enter a name." : null)?.Trim() is not { Length: > 0 } name)
        {
            return;
        }

        var index = SavedSearches.IndexOf(saved);
        saved.Name = name;
        SavedSearches[index] = saved; // a Replace notification refreshes the list and saves the settings
    }

    [RelayCommand]
    private void DeleteSavedSearch(SavedSearch? saved)
    {
        if (saved is not null && _dialogs.Confirm($"Delete the saved search \"{saved.Name}\"? The pages are not affected."))
        {
            SavedSearches.Remove(saved);
        }
    }

    [RelayCommand]
    private void MoveSavedSearchUp(SavedSearch? saved)
    {
        if (saved is not null && SavedSearches.IndexOf(saved) is > 0 and var index)
        {
            SavedSearches.Move(index, index - 1);
        }
    }

    /// <summary>The scope choice for a saved search's folder or page ("In Work › Projects"), replacing an earlier one.</summary>
    private SearchScopeChoice FixedScope(string path)
    {
        var label = "In " + ScopeLabel(path);
        if (SearchScopes.FirstOrDefault(c => c.Value == SearchScopeOption.Fixed) is { } old)
        {
            if (string.Equals(old.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return old;
            }

            SearchScopes.Remove(old);
        }

        var choice = new SearchScopeChoice(SearchScopeOption.Fixed, label, path);
        SearchScopes.Add(choice);
        return choice;
    }

    private string ScopeLabel(string path)
    {
        var name = IsPagePath(path) ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (NotebookOf(path) is not { } notebook)
        {
            return name;
        }

        var relative = Path.GetRelativePath(notebook.Path, path);
        if (relative == ".")
        {
            return notebook.Name;
        }

        var parts = relative.Split(Path.DirectorySeparatorChar);
        parts[^1] = name;
        return notebook.Name + " › " + string.Join(" › ", parts);
    }

    private static bool IsPagePath(string path) => NotebookScanner.IsPageFile(path) && !Directory.Exists(path);

    /// <summary>Picks the table's property columns after a search: keys named in the query, then the most common.</summary>
    private void UpdateTableColumns(string query)
    {
        var fromQuery = SearchQuery.PropertyKeys(SearchQuery.Parse(query)).Distinct(StringComparer.OrdinalIgnoreCase);
        var common = SearchResults.SelectMany(r => r.Properties.Keys)
            .GroupBy(k => k, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => g.Key);
        var columns = fromQuery.Concat(common).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxTableColumns).ToList();
        if (!columns.SequenceEqual(TableColumns))
        {
            TableColumns = columns;
        }
    }
}
