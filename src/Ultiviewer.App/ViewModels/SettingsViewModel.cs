using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Ultiviewer.App.Services;
using Ultiviewer.Core.Files;
using Ultiviewer.Core.Settings;

namespace Ultiviewer.App.ViewModels;

/// <summary>Editable copy of <see cref="AppSettings"/> for the settings window; applied only on Save.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(AppSettings settings, IEnumerable<(string Path, string Name)>? notebooks = null)
    {
        Notebooks = [new("", "First notebook"), .. (notebooks ?? []).Select(n => new KeyValuePair<string, string>(n.Path, n.Name))];
        _dailyNotesNotebook = settings.DailyNotesNotebook ?? "";
        _dailyNotesFolder = settings.DailyNotesFolder;
        _dailyNoteFormat = settings.DailyNoteFormat;
        _quickCaptureHotkey = settings.QuickCaptureHotkey;
        _keepRunningInTray = settings.KeepRunningInTray;
        _theme = settings.Theme;
        _editorFontFamily = settings.EditorFontFamily;
        _editorFontSize = settings.EditorFontSize;
        _wordWrap = settings.WordWrap;
        _showLineNumbers = settings.ShowLineNumbers;
        _showWhitespace = settings.ShowWhitespace;
        _highlightCurrentLine = settings.HighlightCurrentLine;
        _showFormatBar = settings.ShowFormatBar;
        _autosaveNotebookPages = settings.AutosaveNotebookPages;
        _autosaveExternalFiles = settings.AutosaveExternalFiles;
        _autosaveDelayMs = settings.AutosaveDelayMs;
        _keepHistory = settings.KeepHistory;
        _historyIntervalMinutes = settings.HistoryIntervalMinutes;
        _defaultLineEnding = settings.DefaultLineEnding;
        _defaultUtf8Bom = settings.DefaultUtf8Bom;
        _defaultMarkdownViewMode = settings.DefaultMarkdownViewMode;
        _liveFontFamily = settings.LiveFontFamily;
        _liveMaxWidth = settings.LiveMaxWidth;
        _dateFormat = settings.DateFormat;
        _timeFormat = settings.TimeFormat;
        Shortcuts = KeyboardShortcuts.All(settings.Shortcuts);
        IsAssociated = FileAssociation.IsRegistered();
    }

    public IReadOnlyList<ThemePreference> Themes { get; } = Enum.GetValues<ThemePreference>();

    public IReadOnlyList<ViewMode> ViewModes { get; } = [ViewMode.Live, ViewMode.Edit, ViewMode.Split, ViewMode.Preview];

    public IReadOnlyList<KeyValuePair<LineEnding, string>> LineEndings { get; } =
    [
        new(LineEnding.CrLf, "CRLF (Windows)"),
        new(LineEnding.Lf, "LF (Unix, git-friendly)"),
    ];

    /// <summary>Installed fonts, monospaced ones first (they suit an editor).</summary>
    public IReadOnlyList<string> Fonts { get; } = Fonts_();

    public IReadOnlyList<ShortcutEntry> Shortcuts { get; }

    /// <summary>Notebooks for daily notes: path → name ("" = the first notebook).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Notebooks { get; }

    [ObservableProperty]
    private string _dailyNotesNotebook;

    [ObservableProperty]
    private string _dailyNotesFolder;

    [ObservableProperty]
    private string _dailyNoteFormat;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotkeyProblem))]
    private string _quickCaptureHotkey;

    [ObservableProperty]
    private bool _keepRunningInTray;

    /// <summary>Why the quick-note shortcut can't be used, or empty.</summary>
    public string HotkeyProblem => QuickCaptureHotkey.Trim().Length == 0 || GlobalHotkey.TryParse(QuickCaptureHotkey, out _, out _)
        ? ""
        : "Use modifiers and a key, for example Win+Alt+N or Ctrl+Shift+Space.";

    [ObservableProperty]
    private ThemePreference _theme;

    [ObservableProperty]
    private string _editorFontFamily;

    [ObservableProperty]
    private double _editorFontSize;

    [ObservableProperty]
    private bool _wordWrap;

    [ObservableProperty]
    private bool _showLineNumbers;

    [ObservableProperty]
    private bool _showWhitespace;

    [ObservableProperty]
    private bool _highlightCurrentLine;

    [ObservableProperty]
    private bool _showFormatBar;

    [ObservableProperty]
    private bool _autosaveNotebookPages;

    [ObservableProperty]
    private bool _autosaveExternalFiles;

    [ObservableProperty]
    private int _autosaveDelayMs;

    [ObservableProperty]
    private bool _keepHistory;

    [ObservableProperty]
    private int _historyIntervalMinutes;

    [ObservableProperty]
    private LineEnding _defaultLineEnding;

    [ObservableProperty]
    private bool _defaultUtf8Bom;

    [ObservableProperty]
    private ViewMode _defaultMarkdownViewMode;

    [ObservableProperty]
    private string _liveFontFamily;

    [ObservableProperty]
    private int _liveMaxWidth;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DatePreview))]
    private string _dateFormat;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DatePreview))]
    private string _timeFormat;

    [ObservableProperty]
    private bool _isAssociated;

    public string DatePreview
    {
        get
        {
            try
            {
                return $"{DateTime.Now.ToString(DateFormat)}   {DateTime.Now.ToString(TimeFormat)}";
            }
            catch (FormatException)
            {
                return "Invalid format";
            }
        }
    }

    public void ApplyTo(AppSettings settings)
    {
        settings.Theme = Theme;
        settings.EditorFontFamily = string.IsNullOrWhiteSpace(EditorFontFamily) ? "Consolas" : EditorFontFamily;
        settings.EditorFontSize = Math.Clamp(EditorFontSize, 8, 48);
        settings.WordWrap = WordWrap;
        settings.ShowLineNumbers = ShowLineNumbers;
        settings.ShowWhitespace = ShowWhitespace;
        settings.HighlightCurrentLine = HighlightCurrentLine;
        settings.ShowFormatBar = ShowFormatBar;
        settings.AutosaveNotebookPages = AutosaveNotebookPages;
        settings.AutosaveExternalFiles = AutosaveExternalFiles;
        settings.AutosaveDelayMs = Math.Clamp(AutosaveDelayMs, 200, 60000);
        settings.KeepHistory = KeepHistory;
        settings.HistoryIntervalMinutes = Math.Clamp(HistoryIntervalMinutes, 1, 24 * 60);
        settings.DefaultLineEnding = DefaultLineEnding;
        settings.DefaultUtf8Bom = DefaultUtf8Bom;
        settings.DefaultMarkdownViewMode = DefaultMarkdownViewMode;
        settings.LiveFontFamily = string.IsNullOrWhiteSpace(LiveFontFamily) ? "Segoe UI" : LiveFontFamily;
        settings.LiveMaxWidth = LiveMaxWidth <= 0 ? 0 : Math.Clamp(LiveMaxWidth, 300, 4000);
        settings.DailyNotesNotebook = string.IsNullOrWhiteSpace(DailyNotesNotebook) ? null : DailyNotesNotebook;
        settings.DailyNotesFolder = DailyNotesFolder.Trim();
        settings.DailyNoteFormat = string.IsNullOrWhiteSpace(DailyNoteFormat) ? "yyyy-MM-dd" : DailyNoteFormat.Trim();
        settings.QuickCaptureHotkey = HotkeyProblem.Length == 0 ? QuickCaptureHotkey.Trim() : settings.QuickCaptureHotkey;
        settings.KeepRunningInTray = KeepRunningInTray;
        settings.DateFormat = DatePreview == "Invalid format" ? "yyyy-MM-dd" : DateFormat;
        settings.TimeFormat = DatePreview == "Invalid format" ? "HH:mm" : TimeFormat;
    }

    private static IReadOnlyList<string> Fonts_()
    {
        var monospaced = new[] { "Cascadia Mono", "Cascadia Code", "Consolas", "Courier New", "Lucida Console", "JetBrains Mono", "Fira Code", "Source Code Pro" };
        var installed = System.Windows.Media.Fonts.SystemFontFamilies.Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return monospaced.Where(installed.Contains)
            .Concat(installed.Where(f => !monospaced.Contains(f, StringComparer.OrdinalIgnoreCase)).Order(StringComparer.CurrentCultureIgnoreCase))
            .ToList();
    }
}
