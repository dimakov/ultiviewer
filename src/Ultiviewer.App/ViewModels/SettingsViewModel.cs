using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Ultiviewer.App.Services;
using Ultiviewer.Core.Files;
using Ultiviewer.Core.Settings;

namespace Ultiviewer.App.ViewModels;

/// <summary>Editable copy of <see cref="AppSettings"/> for the settings window; applied only on Save.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(AppSettings settings)
    {
        _theme = settings.Theme;
        _editorFontFamily = settings.EditorFontFamily;
        _editorFontSize = settings.EditorFontSize;
        _wordWrap = settings.WordWrap;
        _showLineNumbers = settings.ShowLineNumbers;
        _showWhitespace = settings.ShowWhitespace;
        _highlightCurrentLine = settings.HighlightCurrentLine;
        _autosaveNotebookPages = settings.AutosaveNotebookPages;
        _autosaveExternalFiles = settings.AutosaveExternalFiles;
        _autosaveDelayMs = settings.AutosaveDelayMs;
        _defaultLineEnding = settings.DefaultLineEnding;
        _defaultUtf8Bom = settings.DefaultUtf8Bom;
        _defaultMarkdownViewMode = settings.DefaultMarkdownViewMode;
        _dateFormat = settings.DateFormat;
        _timeFormat = settings.TimeFormat;
        Shortcuts = KeyboardShortcuts.All(settings.Shortcuts);
        IsAssociated = FileAssociation.IsRegistered();
    }

    public IReadOnlyList<ThemePreference> Themes { get; } = Enum.GetValues<ThemePreference>();

    public IReadOnlyList<ViewMode> ViewModes { get; } = Enum.GetValues<ViewMode>();

    public IReadOnlyList<KeyValuePair<LineEnding, string>> LineEndings { get; } =
    [
        new(LineEnding.CrLf, "CRLF (Windows)"),
        new(LineEnding.Lf, "LF (Unix, git-friendly)"),
    ];

    /// <summary>Installed fonts, monospaced ones first (they suit an editor).</summary>
    public IReadOnlyList<string> Fonts { get; } = Fonts_();

    public IReadOnlyList<ShortcutEntry> Shortcuts { get; }

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
    private bool _autosaveNotebookPages;

    [ObservableProperty]
    private bool _autosaveExternalFiles;

    [ObservableProperty]
    private int _autosaveDelayMs;

    [ObservableProperty]
    private LineEnding _defaultLineEnding;

    [ObservableProperty]
    private bool _defaultUtf8Bom;

    [ObservableProperty]
    private ViewMode _defaultMarkdownViewMode;

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
        settings.AutosaveNotebookPages = AutosaveNotebookPages;
        settings.AutosaveExternalFiles = AutosaveExternalFiles;
        settings.AutosaveDelayMs = Math.Clamp(AutosaveDelayMs, 200, 60000);
        settings.DefaultLineEnding = DefaultLineEnding;
        settings.DefaultUtf8Bom = DefaultUtf8Bom;
        settings.DefaultMarkdownViewMode = DefaultMarkdownViewMode;
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
