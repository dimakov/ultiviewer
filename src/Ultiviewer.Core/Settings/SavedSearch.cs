namespace Ultiviewer.Core.Settings;

/// <summary>A named search (a "smart collection"): its query, where it looks and how results are shown.</summary>
public sealed class SavedSearch
{
    public string Name { get; set; } = "";

    public string Query { get; set; } = "";

    /// <summary>A notebook, section folder or page to search in; null searches all notebooks.</summary>
    public string? ScopePath { get; set; }

    /// <summary>Show results as a table with property columns instead of matching lines.</summary>
    public bool ShowAsTable { get; set; }
}
