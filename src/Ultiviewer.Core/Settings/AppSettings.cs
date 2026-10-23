using Ultiviewer.Core.Files;

namespace Ultiviewer.Core.Settings;

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

public enum ViewMode
{
    Edit,
    Split,
    Preview,

    /// <summary>Formatted editing of the source (Obsidian's Live Preview).</summary>
    Live,
}

public sealed class NotebookEntry
{
    public string Path { get; set; } = "";

    /// <summary>Name shown in the tree; the folder name when empty.</summary>
    public string? DisplayName { get; set; }
}

/// <summary>User preferences, stored in <see cref="AppPaths.SettingsFile"/>.</summary>
public sealed class AppSettings
{
    public List<NotebookEntry> Notebooks { get; set; } = [];

    public ThemePreference Theme { get; set; } = ThemePreference.System;

    public string EditorFontFamily { get; set; } = "Cascadia Mono, Consolas";

    public double EditorFontSize { get; set; } = 14;

    public bool WordWrap { get; set; } = true;

    public bool ShowLineNumbers { get; set; } = true;

    public bool ShowWhitespace { get; set; }

    public bool HighlightCurrentLine { get; set; } = true;

    /// <summary>The "Basic Text" formatting toolbar above Markdown documents.</summary>
    public bool ShowFormatBar { get; set; } = true;

    /// <summary>Notebook pages save themselves this long after the last edit.</summary>
    public int AutosaveDelayMs { get; set; } = 1000;

    public bool AutosaveNotebookPages { get; set; } = true;

    /// <summary>Files outside notebooks are saved explicitly unless this is on.</summary>
    public bool AutosaveExternalFiles { get; set; }

    public LineEnding DefaultLineEnding { get; set; } = LineEnding.CrLf;

    public bool DefaultUtf8Bom { get; set; }

    public ViewMode DefaultMarkdownViewMode { get; set; } = ViewMode.Live;

    /// <summary>Proportional font of the Live view; code, tables and front matter keep <see cref="EditorFontFamily"/>.</summary>
    public string LiveFontFamily { get; set; } = "Segoe UI Variable Text, Segoe UI";

    /// <summary>Widest text column of the Live view in pixels, centered; 0 uses the full width.</summary>
    public int LiveMaxWidth { get; set; } = 820;

    public string DateFormat { get; set; } = "yyyy-MM-dd";

    public string TimeFormat { get; set; } = "HH:mm";

    /// <summary>Command name → key gesture text (for example "Ctrl+Alt+N"), overriding the defaults.</summary>
    public Dictionary<string, string> Shortcuts { get; set; } = [];

    public void Save() => JsonFileStore.Save(AppPaths.SettingsFile, this);

    public static AppSettings Load() => JsonFileStore.Load<AppSettings>(AppPaths.SettingsFile);
}
