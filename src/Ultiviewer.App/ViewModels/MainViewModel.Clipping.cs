using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.Input;
using Ultiviewer.App.Services;
using Ultiviewer.Core.Clipping;
using Ultiviewer.Core.Notebooks;

namespace Ultiviewer.App.ViewModels;

/// <summary>"Clip web page…": saves the readable part of a web page (or a saved .html file) as a new page.</summary>
public sealed partial class MainViewModel
{
    private WebFetcher? _webFetcher;

    /// <summary>
    /// Asks for an address (prefilled from the clipboard), downloads the page, extracts the article and creates a
    /// page next to the selected one (or in the selected section) with its pictures in the page's .assets folder.
    /// Pictures that can't be downloaded keep their web address.
    /// </summary>
    [RelayCommand]
    private async Task ClipWebPage(string? address)
    {
        if (TargetNode(null) is not { } target)
        {
            return;
        }

        var input = address ?? _dialogs.Prompt("Clip web page", "Address of the page (or the path of a saved .html file):", ClipboardAddress() ?? "https://",
            text => ParseAddress(text) is null ? "Enter a web address (https://…) or the path of an .html file." : null);
        if (input is null || ParseAddress(input) is not { } uri)
        {
            return;
        }

        var (container, after) = NewPageContainer(target);
        _webFetcher ??= new WebFetcher();
        StatusMessage = $"Clipping {uri}…";
        try
        {
            var page = await _webFetcher.FetchPageAsync(uri);
            var article = await Task.Run(() => WebArticle.Parse(page.Content, page.Charset, page.Url));
            var title = NotebookOperations.UniqueName(container, FileNameRules.MakeValid(article.Title, "Clipped page"));
            var path = Path.Combine(container, title + NotebookScanner.PageExtension);
            var result = await Task.Run(() => article.ToMarkdown(path, DateTime.Now));

            var failed = new ConcurrentBag<ImageDownload>();
            await Parallel.ForEachAsync(result.Images, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (image, cancellation) =>
            {
                try
                {
                    var bytes = await _webFetcher.FetchBytesAsync(image.Source, cancellation);
                    Directory.CreateDirectory(Path.GetDirectoryName(image.TargetPath)!);
                    await File.WriteAllBytesAsync(image.TargetPath, bytes, cancellation);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException or InvalidDataException)
                {
                    failed.Add(image);
                }
            });

            var markdown = failed.Aggregate(result.Markdown, (text, image) => text.Replace("](" + image.Link + ")", "](" + image.Source.AbsoluteUri + ")"));
            string? created = null;
            if (TryRun(() => created = _ops.CreatePage(container, title, markdown, after)))
            {
                RefreshNotebook(target.Root);
                SelectPath(created!);
                OpenDocument(created!, preview: false, focus: true);
                var saved = result.Images.Count - failed.Count;
                StatusMessage = $"Clipped \"{title}\""
                    + (saved > 0 ? $" with {saved} picture{(saved == 1 ? "" : "s")}" : "")
                    + (failed.IsEmpty ? "" : $"; {failed.Count} picture{(failed.Count == 1 ? "" : "s")} couldn't be downloaded and link to the web");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException or InvalidDataException)
        {
            StatusMessage = "";
            ErrorLog.Write(ex, $"Clipping {uri}");
            _dialogs.ShowError($"Couldn't clip {uri}:\n{ex.Message}");
        }
    }

    /// <summary>A web address, a file URI, or the path of an existing .html file; "example.com/x" gets https://.</summary>
    public static Uri? ParseAddress(string text)
    {
        text = text.Trim().Trim('"');
        if (text.Length == 0)
        {
            return null;
        }

        if (Path.IsPathFullyQualified(text))
        {
            return File.Exists(text) ? new Uri(Path.GetFullPath(text)) : null;
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return uri.Scheme is "http" or "https" && uri.Host.Length > 0 || uri.IsFile && File.Exists(uri.LocalPath) ? uri : null;
        }

        return !text.Contains(' ') && text.Contains('.') && Uri.TryCreate("https://" + text, UriKind.Absolute, out var web) && web.Host.Contains('.') ? web : null;
    }

    private static string? ClipboardAddress()
    {
        try
        {
            var text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText().Trim() : "";
            return !text.Contains('\n') && Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? text : null;
        }
        catch (ExternalException)
        {
            return null;
        }
    }

    /// <summary>Where a new page for <paramref name="target"/> goes: into a notebook or section, or after a page (never among templates).</summary>
    private static (string Container, string? After) NewPageContainer(TreeNodeViewModel target)
    {
        var (container, after) = target.Kind is NodeKind.Notebook or NodeKind.Section
            ? (target.Path, (string?)null)
            : (target.Node.ParentFolder, target.Node.EntryName);
        return Core.Files.FileSystemMoves.IsSameOrInside(container, Path.Combine(target.Root.Path, PageTemplates.FolderName))
            ? (target.Root.Path, null)
            : (container, after);
    }
}
