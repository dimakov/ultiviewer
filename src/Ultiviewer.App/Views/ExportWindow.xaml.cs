using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using Ultiviewer.App.Preview;
using Ultiviewer.App.Services;
using Ultiviewer.App.ViewModels;
using Ultiviewer.Core.Markdown;

namespace Ultiviewer.App.Views;

/// <summary>
/// Print preview and export of a page or section: the pages are rendered once in a preview (light theme, math,
/// diagrams and code colors drawn), which is then printed, saved as PDF, or saved as one self-contained HTML file.
/// </summary>
public partial class ExportWindow : Window
{
    private readonly ExportRequest _request;
    private readonly PreviewPane _preview = new();
    private TaskCompletionSource _rendered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ExportWindow(ExportRequest request)
    {
        InitializeComponent();
        _request = request;
        Title = $"Export or print — {request.Title}";
        BreakPages.Visibility = request.IsSection ? Visibility.Visible : Visibility.Collapsed;
        PreviewHost.Child = _preview.View;
        _preview.Rendered += () => _rendered.TrySetResult();
        _preview.LinkClicked += href =>
        {
            if (Uri.TryCreate(href, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
            {
                ShellService.Open(href);
            }
        };
        Loaded += async (_, _) =>
        {
            try
            {
                await _preview.EnsureInitializedAsync();
            }
            catch (Exception ex)
            {
                ErrorLog.Write(ex, "Starting WebView2 for export");
                Status.Text = "Export needs the Microsoft Edge WebView2 Runtime.";
                return;
            }

            _preview.SetTheme(dark: false);
            Render();
            if (_request.PrintWhenReady)
            {
                await _rendered.Task;
                _preview.ShowPrintDialog();
            }
        };
    }

    private bool IsReady => IsLoaded && _preview.IsReady;

    private void Render()
    {
        // A save or print that is already waiting keeps waiting for this render, so only replace a finished one.
        if (_rendered.Task.IsCompleted)
        {
            _rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        var html = HtmlExport.Compose(_request.Pages, TitlePages.IsChecked == true, _request.IsSection && BreakPages.IsChecked == true);
        _preview.Render(html, "https://" + PreviewPane.AppHost + "/", "", "", null);
        Status.Text = _request.Pages.Count == 1 ? "1 page" : $"{_request.Pages.Count} pages";
    }

    private void OnContentOptionChanged(object sender, RoutedEventArgs e)
    {
        if (IsReady)
        {
            Render();
        }
    }

    private async void OnSaveHtml(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "Save as HTML", FileName = SafeName() + ".html", Filter = "Web page|*.html", AddExtension = true };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            await SaveHtmlAsync(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(this, $"Couldn't save the HTML file:\n{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnSavePdf(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "Save as PDF", FileName = SafeName() + ".pdf", Filter = "PDF|*.pdf", AddExtension = true };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            await SavePdfAsync(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            MessageBox.Show(this, $"Couldn't save the PDF:\n{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Writes the rendered document as one self-contained HTML file (styles, fonts and images inside).</summary>
    public async Task SaveHtmlAsync(string path)
    {
        await _rendered.Task;
        var body = await _preview.GetContentHtmlAsync();
        var preview = Path.Combine(AppContext.BaseDirectory, "Preview");
        var styles = new List<string>
        {
            File.ReadAllText(Path.Combine(preview, "preview.css")),
            File.ReadAllText(Path.Combine(preview, "vendor", "highlight", "github.min.css")),
        };
        if (body.Contains("katex", StringComparison.Ordinal))
        {
            var katex = Path.Combine(preview, "vendor", "katex");
            styles.Add(HtmlExport.EmbedFonts(File.ReadAllText(Path.Combine(katex, "katex.min.css")), katex));
        }

        File.WriteAllText(path, HtmlExport.Document(_request.Title, body, styles), new UTF8Encoding(false));
        Status.Text = $"Saved {Path.GetFileName(path)}";
    }

    /// <summary>Prints the rendered document to a PDF with the chosen paper, orientation and margins.</summary>
    public async Task<bool> SavePdfAsync(string path)
    {
        await _rendered.Task;
        var (width, height) = Paper.SelectedIndex == 1 ? (8.5, 11.0) : (8.27, 11.69);
        var margin = Margins.SelectedIndex switch { 1 => 0.4, 2 => 0.0, _ => 0.75 };
        var ok = await _preview.PrintToPdfAsync(path, width, height, margin, landscape: Orientation.SelectedIndex == 1);
        Status.Text = ok ? $"Saved {Path.GetFileName(path)}" : "The PDF couldn't be written.";
        return ok;
    }

    private async void OnPrint(object sender, RoutedEventArgs e)
    {
        await _rendered.Task;
        _preview.ShowPrintDialog();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private string SafeName()
    {
        var name = _request.Title;
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name;
    }
}
