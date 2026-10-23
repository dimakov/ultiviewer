using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Ultiviewer.App.Preview;
using Ultiviewer.App.Services;
using Ultiviewer.App.ViewModels;
using Ultiviewer.Core.Markdown;
using Ultiviewer.Core.Settings;

namespace Ultiviewer.App.Views;

/// <summary>
/// Preview side of the editor host: Edit / Split / Preview layout per tab, debounced background rendering,
/// and two-way scroll sync (whichever side the user scrolled leads; the echo from the other side is ignored).
/// </summary>
public partial class EditorHost
{
    private PreviewPane? _preview;
    private DispatcherTimer? _renderTimer;
    private long _renderVersion;
    private DateTime _ignoreEditorScrollUntil;
    private bool _editorScrollQueued;

    private ViewMode EffectiveMode => _current is { IsMarkdown: true } tab ? tab.ViewMode : ViewMode.Edit;

    private void InitializePreview()
    {
        _renderTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(180), DispatcherPriority.Background, (_, _) =>
        {
            _renderTimer!.Stop();
            RenderPreview(scrollToSource: false);
        }, Dispatcher);
        _renderTimer.Stop();
        Editor.TextArea.TextView.ScrollOffsetChanged += OnEditorScrolled;
        ThemeService.Instance.Changed += () => _preview?.SetTheme(ThemeService.Instance.IsDark);
    }

    private void ApplyViewMode()
    {
        var mode = EffectiveMode;
        var ratio = Math.Clamp(_vm?.Session.PreviewRatio ?? 0.5, 0.15, 0.85);
        switch (mode)
        {
            case ViewMode.Split:
                EditorColumn.Width = new GridLength(ratio, GridUnitType.Star);
                SplitterColumn.Width = GridLength.Auto;
                PreviewColumn.Width = new GridLength(1 - ratio, GridUnitType.Star);
                break;
            case ViewMode.Preview:
                EditorColumn.Width = new GridLength(0);
                SplitterColumn.Width = new GridLength(0);
                PreviewColumn.Width = new GridLength(1, GridUnitType.Star);
                break;
            default:
                EditorColumn.Width = new GridLength(1, GridUnitType.Star);
                SplitterColumn.Width = new GridLength(0);
                PreviewColumn.Width = new GridLength(0);
                break;
        }

        Editor.Visibility = mode == ViewMode.Preview ? Visibility.Collapsed : Visibility.Visible;
        Splitter.Visibility = mode == ViewMode.Split ? Visibility.Visible : Visibility.Collapsed;
        PreviewHost.Visibility = mode == ViewMode.Edit ? Visibility.Collapsed : Visibility.Visible;
        if (mode != ViewMode.Edit)
        {
            ShowPreview();
        }
    }

    private async void ShowPreview()
    {
        if (_preview is null)
        {
            _preview = new PreviewPane();
            _preview.SetTheme(ThemeService.Instance.IsDark);
            _preview.LinkClicked += href => _vm?.OpenLink(_current, href);
            _preview.TaskToggled += line =>
            {
                if (_current is { } tab)
                {
                    _vm?.ToggleTaskAtLine(tab, line);
                }
            };
            _preview.Scrolled += OnPreviewScrolled;
            _preview.LineDoubleClicked += OnPreviewDoubleClicked;
            PreviewHost.Child = _preview.View;
        }

        try
        {
            await _preview.EnsureInitializedAsync();
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "Starting WebView2");
            PreviewHost.Child = new TextBlock
            {
                Margin = new Thickness(20),
                TextWrapping = TextWrapping.Wrap,
                Text = "The preview needs the Microsoft Edge WebView2 Runtime, which is part of Windows 11 and current Windows 10. "
                    + "Install it from https://developer.microsoft.com/microsoft-edge/webview2/ and restart Ultiviewer.\n\n" + ex.Message,
            };
            return;
        }

        RenderPreview(scrollToSource: true);
    }

    private async void RenderPreview(bool scrollToSource)
    {
        if (_preview is null || EffectiveMode == ViewMode.Edit || _current is not { } tab || _vm is null)
        {
            return;
        }

        try
        {
            await _preview.EnsureInitializedAsync();
        }
        catch (Exception)
        {
            return;
        }

        var version = ++_renderVersion;
        var text = tab.Document.Text;
        string html;
        try
        {
            html = await Task.Run(() => MarkdownRenderer.ToHtml(text));
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "Rendering Markdown");
            return;
        }

        if (version != _renderVersion || _current != tab || EffectiveMode == ViewMode.Edit)
        {
            return;
        }

        var root = tab.Path is null ? null : _vm.NotebookOf(tab.Path);
        double? line = null;
        if (scrollToSource)
        {
            line = EffectiveMode == ViewMode.Split ? EditorTopLine() : tab.PreviewLine;
        }

        _preview.Render(html, _preview.BaseUrlFor(tab.Path, root?.Path), tab.Title, PreviewMeta(tab, root), line);
    }

    private static string PreviewMeta(DocumentTabViewModel tab, TreeNodeViewModel? notebook)
    {
        if (tab.Path is null)
        {
            return "Not saved yet";
        }

        var folder = Path.GetDirectoryName(tab.Path)!;
        var where = notebook is null
            ? folder
            : string.Join(" › ", new[] { notebook.Name }.Concat(Path.GetRelativePath(notebook.Path, folder).Split(Path.DirectorySeparatorChar).Where(s => s != ".")));
        var edited = File.Exists(tab.Path) ? File.GetLastWriteTime(tab.Path).ToString("dddd, d MMMM yyyy · HH:mm") : "";
        return edited.Length > 0 ? $"{where} · {edited}" : where;
    }

    private void OnDocumentEdited(DocumentTabViewModel tab)
    {
        if (tab == _current && EffectiveMode != ViewMode.Edit && _renderTimer is not null)
        {
            _renderTimer.Stop();
            _renderTimer.Start();
        }
    }

    // ---- Scroll sync ----

    private double EditorTopLine()
    {
        var view = Editor.TextArea.TextView;
        var y = view.ScrollOffset.Y;
        var line = view.GetDocumentLineByVisualTop(y);
        if (line is null)
        {
            return 1;
        }

        var top = view.GetVisualTopByDocumentLine(line.LineNumber);
        var bottom = line.NextLine is { } next ? view.GetVisualTopByDocumentLine(next.LineNumber) : top + view.DefaultLineHeight;
        return line.LineNumber + (bottom > top ? Math.Clamp((y - top) / (bottom - top), 0, 1) : 0);
    }

    private void ScrollEditorToLine(double line)
    {
        var document = Editor.Document;
        if (document.LineCount == 0)
        {
            return;
        }

        var number = Math.Clamp((int)Math.Floor(line), 1, document.LineCount);
        var fraction = Math.Clamp(line - number, 0, 1);
        var view = Editor.TextArea.TextView;
        var top = view.GetVisualTopByDocumentLine(number);
        var nextTop = number < document.LineCount ? view.GetVisualTopByDocumentLine(number + 1) : top + view.DefaultLineHeight;
        Editor.ScrollToVerticalOffset(top + fraction * (nextTop - top));
    }

    private void OnEditorScrolled(object? sender, EventArgs e)
    {
        if (_preview is null || EffectiveMode != ViewMode.Split || DateTime.UtcNow < _ignoreEditorScrollUntil || _editorScrollQueued)
        {
            return;
        }

        _editorScrollQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _editorScrollQueued = false;
            var line = EditorTopLine();
            if (_current is { } tab)
            {
                tab.PreviewLine = line;
            }

            _preview.ScrollToLine(line);
        }, DispatcherPriority.Render);
    }

    private void OnPreviewScrolled(double line)
    {
        if (_current is { } tab)
        {
            tab.PreviewLine = line;
        }

        if (EffectiveMode == ViewMode.Split)
        {
            _ignoreEditorScrollUntil = DateTime.UtcNow.AddMilliseconds(250);
            ScrollEditorToLine(line);
        }
    }

    private void OnPreviewDoubleClicked(int line)
    {
        if (_current is not { } tab || _vm is null)
        {
            return;
        }

        if (tab.ViewMode == ViewMode.Preview)
        {
            _vm.SetTabViewMode(tab, ViewMode.Split);
        }

        GoToLine(line);
        FocusEditor();
    }

    private void SavePreviewRatio()
    {
        var editor = EditorColumn.ActualWidth;
        var preview = PreviewColumn.ActualWidth;
        if (_vm is not null && editor + preview > 0 && EffectiveMode == ViewMode.Split)
        {
            _vm.Session.PreviewRatio = editor / (editor + preview);
        }
    }
}
