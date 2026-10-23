using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Ultiviewer.Core.Settings;

namespace Ultiviewer.App.Services;

/// <summary>Colors the editor and preview use, per theme.</summary>
public sealed record EditorPalette(
    Color Background,
    Color Foreground,
    Color LineNumbers,
    Color CurrentLine,
    Color Selection,
    Color Match,
    Color Bookmark)
{
    public static EditorPalette Light { get; } = new(
        Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0x1F, 0x1F, 0x1F), Color.FromRgb(0x8A, 0x8A, 0x8A),
        Color.FromRgb(0xF2, 0xF4, 0xF7), Color.FromRgb(0xAD, 0xD6, 0xFF), Color.FromArgb(0x90, 0xFF, 0xD3, 0x3D),
        Color.FromRgb(0x3A, 0x7B, 0xD5));

    public static EditorPalette Dark { get; } = new(
        Color.FromRgb(0x1E, 0x1E, 0x1E), Color.FromRgb(0xD8, 0xD8, 0xD8), Color.FromRgb(0x85, 0x85, 0x85),
        Color.FromRgb(0x2A, 0x2D, 0x2E), Color.FromRgb(0x26, 0x4F, 0x78), Color.FromArgb(0x70, 0xB8, 0x8A, 0x00),
        Color.FromRgb(0x5A, 0x9B, 0xF0));
}

/// <summary>
/// Applies the theme preference (System / Light / Dark) to WPF's Fluent ThemeMode and tells the editor and
/// preview whether the effective theme is dark, including when Windows switches while the app runs.
/// </summary>
public sealed class ThemeService
{
    private ThemePreference _preference;

    public static ThemeService Instance { get; } = new();

    private ThemeService()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
            {
                Application.Current?.Dispatcher.BeginInvoke(Update);
            }
        };
    }

    public bool IsDark { get; private set; }

    public EditorPalette Palette => IsDark ? EditorPalette.Dark : EditorPalette.Light;

    public event Action? Changed;

    public void Apply(ThemePreference preference)
    {
        _preference = preference;
        Application.Current.ThemeMode = preference switch
        {
            ThemePreference.Light => ThemeMode.Light,
            ThemePreference.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
        Update();
    }

    private void Update()
    {
        var dark = _preference switch
        {
            ThemePreference.Dark => true,
            ThemePreference.Light => false,
            _ => SystemUsesDarkTheme(),
        };
        if (dark != IsDark)
        {
            IsDark = dark;
            Changed?.Invoke();
        }
    }

    private static bool SystemUsesDarkTheme() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0;
}
