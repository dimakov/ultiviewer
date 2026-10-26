using System.ComponentModel;
using System.Windows;
using Ultiviewer.App.Preview;
using Ultiviewer.App.Services;
using Ultiviewer.App.ViewModels;
using Ultiviewer.Core.Markdown;

namespace Ultiviewer.App.Views;

/// <summary>
/// Import from OneNote: the notebook's sections and pages with check boxes on the left; the selected page as it
/// will be written on the right (rendered by the normal preview, and as Markdown), with what isn't converted
/// exactly below it. Modeless; closing it stops reading and deletes the temporary files.
/// </summary>
public partial class OneNoteImportWindow : Window
{
    private readonly OneNoteImportViewModel _vm;
    private readonly PreviewPane _preview = new();
    private bool _previewFailed;
    private bool _closed;
    private int _renders;

    public OneNoteImportWindow(OneNoteImportViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        PreviewHost.Child = _preview.View;
        _preview.View.Visibility = Visibility.Hidden;
        _preview.LinkClicked += href =>
        {
            if (Uri.TryCreate(href, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
            {
                ShellService.Open(href);
            }
        };
        _preview.Rendered += () => PreviewShown?.Invoke();
        vm.PreviewReady += OnPreviewReady;
        Loaded += async (_, _) =>
        {
            _ = vm.ScanAsync();
            try
            {
                await _preview.EnsureInitializedAsync();
                _preview.SetTheme(ThemeService.Instance.IsDark);
            }
            catch (Exception ex)
            {
                ErrorLog.Write(ex, "Starting WebView2 for the OneNote import");
                _previewFailed = true;
                PreviewHint.Text = "The preview needs the Microsoft Edge WebView2 Runtime. The Markdown tab shows the page.";
            }
        };
        Closing += OnClosing;
    }

    public OneNoteImportViewModel ViewModel => _vm;

    /// <summary>Raised when the preview has drawn the selected page (for the snapshot tool).</summary>
    public event Action? PreviewShown;

    /// <summary>Saves the rendered page as a PNG (for the snapshot tool).</summary>
    public Task CapturePreviewAsync(string path) => _preview.CaptureAsync(path);

    private async void OnPreviewReady(OneNotePagePreview? page)
    {
        var render = ++_renders;
        if (page is null)
        {
            _preview.View.Visibility = Visibility.Hidden;
            PreviewHint.Visibility = Visibility.Visible;
            return;
        }

        var html = await Task.Run(() => MarkdownRenderer.ToHtml(page.Markdown));
        if (_previewFailed || render != _renders)
        {
            return;
        }

        try
        {
            await _preview.EnsureInitializedAsync();
        }
        catch (Exception)
        {
            return; // reported when the window loaded
        }

        if (render != _renders)
        {
            return;
        }

        PreviewHint.Visibility = Visibility.Collapsed;
        _preview.View.Visibility = Visibility.Visible;
        _preview.Render(html, _preview.BaseUrlFor(page.PagePath, page.Root), "", "", null);
    }

    private void OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        _vm.Selected = e.NewValue as OneNoteImportItem;

    private void OnChangeFolder(object sender, RoutedEventArgs e) => _vm.PickTargetFolder();

    private async void OnImport(object sender, RoutedEventArgs e) => await _vm.ImportAsync();

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closed)
        {
            return;
        }

        // Reading and importing stop first, so the temporary files they use can be deleted.
        e.Cancel = true;
        _closed = true;
        _vm.PreviewReady -= OnPreviewReady;
        _vm.CancelImport();
        await _vm.CloseAsync();
        _ = Dispatcher.BeginInvoke(Close); // after this handler returns, even when nothing had to be awaited
    }
}
