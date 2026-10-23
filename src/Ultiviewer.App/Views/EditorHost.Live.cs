using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Ultiviewer.App.Editor.Live;
using Ultiviewer.App.Services;
using Ultiviewer.Core.Settings;

namespace Ultiviewer.App.Views;

/// <summary>
/// The Live view mode: formatted editing in the same AvalonEdit control, with a proportional font, a centered
/// text column and no line numbers. Rendering lives in <see cref="LivePreview"/>.
/// </summary>
public partial class EditorHost
{
    private LivePreview? _live;

    private void InitializeLive()
    {
        _live = new LivePreview(Editor, CreateLiveStyles())
        {
            BaseDirectory = () => _current?.Path is { } path ? Path.GetDirectoryName(path) : null,
        };
        _live.LinkClicked += href => _vm?.OpenLink(_current, href);
        _live.TaskToggled += line =>
        {
            if (_current is { } tab)
            {
                _vm?.ToggleTaskAtLine(tab, line);
            }
        };
        Editor.SizeChanged += (_, _) => UpdateLiveMargins();
        Editor.PreviewMouseLeftButtonDown += OnLiveMarginMouseDown;
    }

    private LiveStyles CreateLiveStyles() => LiveStyles.Create(
        ThemeService.Instance.Palette,
        ThemeService.Instance.IsDark,
        Font(_vm?.Settings.LiveFontFamily, "Segoe UI"),
        Font(_vm?.Settings.EditorFontFamily, "Consolas"));

    private static FontFamily Font(string? name, string fallback)
    {
        try
        {
            return new FontFamily(string.IsNullOrWhiteSpace(name) ? fallback : name);
        }
        catch (ArgumentException)
        {
            return new FontFamily(fallback);
        }
    }

    /// <summary>Font, wrapping, margins and highlighting for the current tab's mode (Live differs from the others).</summary>
    private void ApplyEditorAppearance()
    {
        if (_vm is null || _live is null)
        {
            return;
        }

        var settings = _vm.Settings;
        var live = EffectiveMode == ViewMode.Live;
        Editor.FontFamily = live ? _live.Styles.TextFont : Font(settings.EditorFontFamily, "Consolas");
        Editor.WordWrap = live || settings.WordWrap;
        Editor.ShowLineNumbers = !live && settings.ShowLineNumbers;
        Editor.Options.HighlightCurrentLine = !live && settings.HighlightCurrentLine;
        if (live)
        {
            _live.Enable();
        }
        else
        {
            _live.Disable();
        }

        ApplyHighlighting();
        UpdateLiveMargins();
    }

    /// <summary>In Live mode the text sits in a centered column at most <see cref="AppSettings.LiveMaxWidth"/> wide.</summary>
    private void UpdateLiveMargins()
    {
        var view = Editor.TextArea.TextView;
        if (_live is not { IsEnabled: true })
        {
            view.Margin = default;
            return;
        }

        var minimum = LiveBackgroundRenderer.Bleed + 16;
        var maxWidth = _vm?.Settings.LiveMaxWidth ?? 0;
        var available = Editor.ActualWidth - SystemParameters.VerticalScrollBarWidth;
        var side = maxWidth > 0 ? Math.Max(minimum, (available - maxWidth) / 2) : minimum;
        view.Margin = new Thickness(Math.Round(side), 0, Math.Round(side), 0);
    }

    /// <summary>A click in the empty margins beside the Live column puts the caret on the nearest text.</summary>
    private void OnLiveMarginMouseDown(object sender, MouseButtonEventArgs e)
    {
        var view = Editor.TextArea.TextView;
        if (_live is not { IsEnabled: true } || e.OriginalSource is not DependencyObject source
            || IsWithin(source, view) || (IsWithin(source, Editor.TextArea) && !ReferenceEquals(source, Editor.TextArea)) || IsWithin<ScrollBar>(source))
        {
            return;
        }

        var point = e.GetPosition(view);
        if (point.Y < 0 || point.Y > view.ActualHeight || view.ActualWidth <= 1)
        {
            return;
        }

        var clamped = new Point(Math.Clamp(point.X, 0, view.ActualWidth - 1), point.Y) + view.ScrollOffset;
        if (view.GetPosition(clamped) is { } position)
        {
            Editor.TextArea.ClearSelection();
            Editor.TextArea.Caret.Position = position;
            Editor.TextArea.Focus();
            e.Handled = true;
        }
    }

    private static bool IsWithin(DependencyObject element, DependencyObject ancestor) => Ancestors(element).Any(a => ReferenceEquals(a, ancestor));

    private static bool IsWithin<T>(DependencyObject element) => Ancestors(element).OfType<T>().Any();

    /// <summary>The element and its parents; text elements such as <c>Run</c> are followed through the logical tree.</summary>
    private static IEnumerable<DependencyObject> Ancestors(DependencyObject element)
    {
        for (var current = element; current is not null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            yield return current;
        }
    }
}
