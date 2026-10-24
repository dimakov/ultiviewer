namespace Ultiviewer.Core.Settings;

public sealed class WindowPlacement
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 1280;
    public double Height { get; set; } = 820;
    public bool Maximized { get; set; }
}

public sealed class OpenTabState
{
    /// <summary>File path; empty for an untitled buffer.</summary>
    public string Path { get; set; } = "";

    public string? UntitledName { get; set; }

    public string? Language { get; set; }
    public int CaretOffset { get; set; }
    public double VerticalOffset { get; set; }
    public ViewMode? ViewMode { get; set; }
    public bool Pinned { get; set; }

    /// <summary>Unsaved text snapshot in the backup folder, restored after a crash or exit.</summary>
    public string? BackupFile { get; set; }

    /// <summary>Bookmarked line numbers (1-based).</summary>
    public List<int> Bookmarks { get; set; } = [];
}

/// <summary>Per-machine UI state, stored in <see cref="AppPaths.SessionFile"/>.</summary>
public sealed class SessionState
{
    public WindowPlacement Window { get; set; } = new();

    public double TreeWidth { get; set; } = 280;

    public double PreviewRatio { get; set; } = 0.5;

    public double ResultsHeight { get; set; } = 200;

    public List<string> ExpandedPaths { get; set; } = [];

    public string? SelectedPath { get; set; }

    public List<OpenTabState> Tabs { get; set; } = [];

    public int ActiveTab { get; set; } = -1;

    /// <summary>Two editors: the second one's tabs (a document may be in both lists).</summary>
    public bool IsSplit { get; set; }

    /// <summary>Side by side (true) or one above the other.</summary>
    public bool SplitVertical { get; set; } = true;

    /// <summary>Share of the first editor, 0–1.</summary>
    public double SplitRatio { get; set; } = 0.5;

    public List<OpenTabState> SplitTabs { get; set; } = [];

    public int SplitActiveTab { get; set; } = -1;

    /// <summary>0 = first editor, 1 = second.</summary>
    public int FocusedGroup { get; set; }

    public List<string> RecentPages { get; set; } = [];

    public List<string> FavoritePages { get; set; } = [];

    public List<string> RecentSearches { get; set; } = [];

    public void Save() => JsonFileStore.Save(AppPaths.SessionFile, this);

    public static SessionState Load() => JsonFileStore.Load<SessionState>(AppPaths.SessionFile);
}
