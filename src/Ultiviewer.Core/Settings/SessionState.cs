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
    public string Path { get; set; } = "";
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

    public List<string> RecentPages { get; set; } = [];

    public List<string> FavoritePages { get; set; } = [];

    public List<string> RecentSearches { get; set; } = [];

    public void Save() => JsonFileStore.Save(AppPaths.SessionFile, this);

    public static SessionState Load() => JsonFileStore.Load<SessionState>(AppPaths.SessionFile);
}
