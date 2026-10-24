using CommunityToolkit.Mvvm.Input;
using Ultiviewer.App.Services;
using Ultiviewer.Core.Settings;

namespace Ultiviewer.App.ViewModels;

/// <summary>View options (menu toggles) that map onto <see cref="AppSettings"/>.</summary>
public sealed partial class MainViewModel
{
    private const double MinFontSize = 8;
    private const double MaxFontSize = 48;

    /// <summary>Raised when an editor setting changes, so the editor re-applies its options.</summary>
    public event Action? EditorSettingsChanged;

    public bool WordWrap
    {
        get => Settings.WordWrap;
        set => SetEditorSetting(Settings.WordWrap, value, v => Settings.WordWrap = v);
    }

    public bool ShowLineNumbers
    {
        get => Settings.ShowLineNumbers;
        set => SetEditorSetting(Settings.ShowLineNumbers, value, v => Settings.ShowLineNumbers = v);
    }

    public bool ShowWhitespace
    {
        get => Settings.ShowWhitespace;
        set => SetEditorSetting(Settings.ShowWhitespace, value, v => Settings.ShowWhitespace = v);
    }

    public bool HighlightCurrentLine
    {
        get => Settings.HighlightCurrentLine;
        set => SetEditorSetting(Settings.HighlightCurrentLine, value, v => Settings.HighlightCurrentLine = v);
    }

    public bool ShowFormatBar
    {
        get => Settings.ShowFormatBar;
        set => SetEditorSetting(Settings.ShowFormatBar, value, v => Settings.ShowFormatBar = v);
    }

    public int ZoomPercent => (int)Math.Round(Settings.EditorFontSize / 14.0 * 100);

    public bool IsThemeSystem => Settings.Theme == ThemePreference.System;

    public bool IsThemeLight => Settings.Theme == ThemePreference.Light;

    public bool IsThemeDark => Settings.Theme == ThemePreference.Dark;

    private void SetEditorSetting(bool current, bool value, Action<bool> set, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (current == value)
        {
            return;
        }

        set(value);
        OnPropertyChanged(name);
        Settings.Save();
        EditorSettingsChanged?.Invoke();
    }

    [RelayCommand]
    private void ZoomIn() => SetFontSize(Settings.EditorFontSize + 1);

    [RelayCommand]
    private void ZoomOut() => SetFontSize(Settings.EditorFontSize - 1);

    [RelayCommand]
    private void ZoomReset() => SetFontSize(14);

    public void SetFontSize(double size)
    {
        Settings.EditorFontSize = Math.Clamp(size, MinFontSize, MaxFontSize);
        OnPropertyChanged(nameof(ZoomPercent));
        EditorSettingsChanged?.Invoke();
    }

    [RelayCommand]
    private void SetTheme(string name)
    {
        if (!Enum.TryParse<ThemePreference>(name, out var theme))
        {
            return;
        }

        Settings.Theme = theme;
        Settings.Save();
        ThemeService.Instance.Apply(theme);
        OnPropertyChanged(nameof(IsThemeSystem));
        OnPropertyChanged(nameof(IsThemeLight));
        OnPropertyChanged(nameof(IsThemeDark));
    }

    /// <summary>Asks the view to show the settings window, optionally at a section ("shortcuts").</summary>
    public event Action<string?>? SettingsRequested;

    [RelayCommand]
    private void OpenSettings(string? section) => SettingsRequested?.Invoke(section);

    /// <summary>Asks the view to show the command palette (Ctrl+Shift+P).</summary>
    public event Action? CommandPaletteRequested;

    [RelayCommand]
    private void ShowCommandPalette() => CommandPaletteRequested?.Invoke();

    /// <summary>Re-reads all settings after the settings window saved them.</summary>
    public void ReloadSettingsIntoView()
    {
        foreach (var name in new[] { nameof(WordWrap), nameof(ShowLineNumbers), nameof(ShowWhitespace), nameof(HighlightCurrentLine), nameof(ShowFormatBar), nameof(ZoomPercent), nameof(IsThemeSystem), nameof(IsThemeLight), nameof(IsThemeDark) })
        {
            OnPropertyChanged(name);
        }

        ThemeService.Instance.Apply(Settings.Theme);
        foreach (var tab in OpenTabs)
        {
            UpdateNotebookPageFlag(tab); // autosave rules may have changed
        }

        EditorSettingsChanged?.Invoke();
        CaptureSettingsChanged?.Invoke();
        if (_search is { } search)
        {
            search.IncludeAttachments = Settings.SearchAttachments;
            RequestAttachmentIndexing();
        }
    }
}
