using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Ultiviewer.App.Services;
using Ultiviewer.Core.Settings;

namespace Ultiviewer.App.Preview;

/// <summary>
/// The WebView2 that shows rendered Markdown. It loads one shell page (<c>Preview/preview.html</c>) from a
/// virtual host and afterwards only exchanges JSON messages with it, so updates never reload the page.
/// Each notebook (or folder of an outside file) gets its own host name, served by <see cref="OnWebResourceRequested"/>
/// (virtual-host mappings added after the page loaded are not honoured), so relative image paths work.
/// </summary>
public sealed class PreviewPane
{
    public const string AppHost = "app.ultiviewer.example";
    private const string ShellUrl = "https://" + AppHost + "/preview.html";
    private const string HostSuffix = ".ultiviewer.example";

    private readonly WebView2 _web = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _hostFolders = new(StringComparer.OrdinalIgnoreCase);
    private Task? _initialization;
    private bool _ready;
    private string? _pendingRender;
    private bool _dark;

    public PreviewPane()
    {
        _web.DefaultBackgroundColor = System.Drawing.Color.Transparent;
    }

    public FrameworkElement View => _web;

    public bool IsReady => _ready;

    public event Action<string>? LinkClicked;

    public event Action<int>? TaskToggled;

    public event Action<double>? Scrolled;

    public event Action<int>? LineDoubleClicked;

    /// <summary>Raised when a render has finished drawing (code colors, math and diagrams included).</summary>
    public event Action? Rendered;

    /// <summary>The rendered content's HTML (after math and diagrams were drawn), for export.</summary>
    public async Task<string> GetContentHtmlAsync()
    {
        var json = await _web.CoreWebView2.ExecuteScriptAsync("document.getElementById('content').innerHTML");
        return JsonSerializer.Deserialize<string>(json) ?? "";
    }

    /// <summary>Prints the page to a PDF file: paper size and margins in inches.</summary>
    public async Task<bool> PrintToPdfAsync(string path, double pageWidth, double pageHeight, double margin, bool landscape)
    {
        var settings = _web.CoreWebView2.Environment.CreatePrintSettings();
        settings.Orientation = landscape ? CoreWebView2PrintOrientation.Landscape : CoreWebView2PrintOrientation.Portrait;
        settings.PageWidth = pageWidth;
        settings.PageHeight = pageHeight;
        settings.MarginTop = settings.MarginBottom = settings.MarginLeft = settings.MarginRight = margin;
        settings.ShouldPrintBackgrounds = true;
        settings.ShouldPrintHeaderAndFooter = false;
        return await _web.CoreWebView2.PrintToPdfAsync(path, settings);
    }

    /// <summary>Opens the browser print dialog (printer, pages, preview).</summary>
    public void ShowPrintDialog() => _web.CoreWebView2.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);

    /// <summary>Starts WebView2 once. Throws when the WebView2 runtime is missing.</summary>
    public Task EnsureInitializedAsync() => _initialization ??= InitializeAsync();

    private async Task InitializeAsync()
    {
        var environment = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewDataDirectory);
        await _web.EnsureCoreWebView2Async(environment);
        var core = _web.CoreWebView2;

        var settings = core.Settings;
        settings.AreDevToolsEnabled = Environment.GetEnvironmentVariable("ULTIVIEWER_DEVTOOLS") == "1";
        settings.AreHostObjectsAllowed = false;
        settings.IsStatusBarEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsZoomControlEnabled = true;
        settings.IsWebMessageEnabled = true;

        core.SetVirtualHostNameToFolderMapping(AppHost, Path.Combine(AppContext.BaseDirectory, "Preview"), CoreWebView2HostResourceAccessKind.Allow);
        core.AddWebResourceRequestedFilter("https://f*" + HostSuffix + "/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.WebMessageReceived += OnWebMessage;
        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            LinkClicked?.Invoke(e.Uri);
        };
        core.ContextMenuRequested += OnContextMenuRequested;
        core.Profile.PreferredColorScheme = _dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
        core.Navigate(ShellUrl);
    }

    public void Render(string html, string baseUrl, string title, string meta, double? line)
    {
        var json = JsonSerializer.Serialize(new { type = "render", html, baseUrl, title, meta, line });
        if (_ready)
        {
            Post(json);
        }
        else
        {
            _pendingRender = json;
        }
    }

    public void ScrollToLine(double line)
    {
        if (_ready)
        {
            Post(JsonSerializer.Serialize(new { type = "scroll", line }));
        }
    }

    public void SetTheme(bool dark)
    {
        _dark = dark;
        var background = dark ? EditorPalette.Dark.Background : EditorPalette.Light.Background;
        _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(background.R, background.G, background.B);
        if (_web.CoreWebView2 is { } core)
        {
            core.Profile.PreferredColorScheme = dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
        }

        if (_ready)
        {
            Post(JsonSerializer.Serialize(new { type = "theme", theme = dark ? "dark" : "light" }));
        }
    }

    /// <summary>
    /// Base URL for resolving a document's relative links: the notebook root (or the file's folder) is a
    /// virtual host, and the URL path is the document's folder inside it, so <c>../other/img.png</c> works.
    /// </summary>
    public string BaseUrlFor(string? filePath, string? notebookRoot)
    {
        if (filePath is null || _web.CoreWebView2 is null)
        {
            return $"https://{AppHost}/";
        }

        var folder = Path.GetDirectoryName(filePath)!;
        var root = notebookRoot ?? folder;
        var host = HostFor(root);
        var relative = Path.GetRelativePath(root, folder);
        var path = relative == "." ? "" : string.Join("/", relative.Split(Path.DirectorySeparatorChar).Select(Uri.EscapeDataString)) + "/";
        return $"https://{host}/{path}";
    }

    private string HostFor(string folder)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folder.ToUpperInvariant())))[..12].ToLowerInvariant();
        var host = "f" + hash + HostSuffix;
        _hostFolders[host] = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return host;
    }

    /// <summary>Serves files of mapped folders; anything outside the folder or missing is a 404.</summary>
    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var environment = _web.CoreWebView2.Environment;
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) || !_hostFolders.TryGetValue(uri.Host, out var folder))
        {
            e.Response = environment.CreateWebResourceResponse(null, 404, "Not Found", "");
            return;
        }

        try
        {
            var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            var path = Path.GetFullPath(Path.Combine(folder, relative));
            if (!path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            {
                e.Response = environment.CreateWebResourceResponse(null, 404, "Not Found", "");
                return;
            }

            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            e.Response = environment.CreateWebResourceResponse(stream, 200, "OK", $"Content-Type: {ContentType(path)}\r\nCache-Control: no-cache");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            e.Response = environment.CreateWebResourceResponse(null, 404, "Not Found", "");
        }
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".bmp" => "image/bmp",
        ".ico" => "image/x-icon",
        ".avif" => "image/avif",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".ogg" => "audio/ogg",
        ".pdf" => "application/pdf",
        ".css" => "text/css",
        ".txt" or ".md" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };

    private void Post(string json)
    {
        try
        {
            _web.CoreWebView2?.PostWebMessageAsJson(json);
        }
        catch (InvalidOperationException ex)
        {
            ErrorLog.Write(ex, "Posting to preview");
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var message = document.RootElement;
            switch (message.GetProperty("type").GetString())
            {
                case "ready":
                    _ready = true;
                    SetTheme(_dark);
                    if (_pendingRender is { } pending)
                    {
                        _pendingRender = null;
                        Post(pending);
                    }

                    break;
                case "link":
                    LinkClicked?.Invoke(message.GetProperty("href").GetString() ?? "");
                    break;
                case "rendered":
                    Rendered?.Invoke();
                    break;
                case "toggleTask":
                    TaskToggled?.Invoke(message.GetProperty("line").GetInt32());
                    break;
                case "scrolled":
                    Scrolled?.Invoke(message.GetProperty("line").GetDouble());
                    break;
                case "dblclick":
                    LineDoubleClicked?.Invoke(message.GetProperty("line").GetInt32());
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            ErrorLog.Write(ex, "Preview message");
        }
    }

    /// <summary>The shell page never navigates; any other navigation is treated as a link click.</summary>
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (e.Uri.StartsWith(ShellUrl, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        e.Cancel = true;
        if (e.IsUserInitiated)
        {
            LinkClicked?.Invoke(e.Uri);
        }
    }

    /// <summary>Keeps only copy/select-all style entries in the page's right-click menu.</summary>
    private static void OnContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "copy", "copyImage", "copyLinkLocation", "selectAll", "copyImageLocation" };
        for (var i = e.MenuItems.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(e.MenuItems[i].Name))
            {
                e.MenuItems.RemoveAt(i);
            }
        }
    }
}
