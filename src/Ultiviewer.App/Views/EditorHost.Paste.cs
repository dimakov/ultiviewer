using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using Ultiviewer.App.Services;
using Ultiviewer.Core.Clipping;
using Ultiviewer.Core.Markdown;
using Ultiviewer.Core.Notebooks;

namespace Ultiviewer.App.Views;

/// <summary>
/// Formatted paste: HTML copied from a browser, Word or Google Docs is pasted as Markdown in notes; inline images
/// (data URIs) and Word's temporary picture files are saved as attachments. Plain-looking HTML, text copied from
/// this editor and pastes inside code blocks stay plain text; Ctrl+Alt+V always pastes plain text.
/// </summary>
public partial class EditorHost
{
    /// <summary>Marks text copied from our own editor: it is Markdown already, and its HTML is only syntax colouring.</summary>
    private const string OwnCopyFormat = "Ultiviewer.MarkdownSource";

    private void OnCopying(object sender, DataObjectCopyingEventArgs e)
    {
        try
        {
            e.DataObject.SetData(OwnCopyFormat, "1");
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
        }
    }

    private bool PasteFormatted()
    {
        if (_vm?.Settings.PasteHtmlAsMarkdown != true || TableEditor.IsInsideFence(Editor.Document.Text, Editor.CaretOffset))
        {
            return false;
        }

        try
        {
            return !Clipboard.ContainsData(OwnCopyFormat) && Clipboard.ContainsData(DataFormats.Html) && PasteHtml(Clipboard.GetData(DataFormats.Html) as string);
        }
        catch (ExternalException ex)
        {
            ErrorLog.Write(ex, "Reading HTML from the clipboard");
            return false;
        }
    }

    /// <summary>
    /// Inserts clipboard HTML (CF_HTML, or plain HTML) as Markdown. Returns false, inserting nothing, when it has
    /// no formatting worth keeping. Internal so the snapshot tool can paste without touching the real clipboard.
    /// </summary>
    internal bool PasteHtml(string? clipboardHtml)
    {
        if (ClipboardHtml.Parse(clipboardHtml) is not { } fragment)
        {
            return false;
        }

        var saved = 0;
        var converter = new HtmlToMarkdown(fragment.SourceUrl, (source, _) =>
        {
            var link = PastedImage(source);
            if (link is not null && link != source)
            {
                saved++;
            }

            return link;
        });
        var result = converter.Convert(fragment.Html);
        if (!result.HasFormatting || result.Markdown.Length == 0)
        {
            return false;
        }

        var newLine = NewLineAt(Editor.TextArea.Caret.Line);
        var markdown = result.Markdown.Replace("\n", newLine);
        if (result.Markdown.Contains('\n') || BlockSyntax().IsMatch(result.Markdown))
        {
            InsertBlock(markdown, newLine);
        }
        else
        {
            Editor.TextArea.Selection.ReplaceSelectionWithText(markdown);
        }

        _vm!.StatusMessage = "Pasted as Markdown" + (saved > 0 ? $", {saved} picture{(saved == 1 ? "" : "s")} saved with the page" : "") + " (Ctrl+Alt+V pastes plain text)";
        return true;
    }

    /// <summary>
    /// The link for a pasted picture: inline data and local files (Word's clip_image files) are saved next to the
    /// page; web pictures keep their address. Null leaves a picture out (inline data in an unsaved page).
    /// </summary>
    private string? PastedImage(string source)
    {
        var page = _current?.Path;
        try
        {
            if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (page is null || DataUri().Match(source) is not { Success: true } data)
                {
                    return null;
                }

                var bytes = data.Groups["base64"].Success
                    ? Convert.FromBase64String(Uri.UnescapeDataString(data.Groups["data"].Value))
                    : System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(data.Groups["data"].Value));
                var extension = data.Groups["type"].Value.ToLowerInvariant() switch
                {
                    "jpeg" or "jpg" => ".jpg",
                    "gif" => ".gif",
                    "webp" => ".webp",
                    "svg+xml" => ".svg",
                    "bmp" => ".bmp",
                    _ => ".png",
                };
                return Save(page, Path.ChangeExtension(PageAssets.ScreenshotName(DateTime.Now), extension), bytes);
            }

            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                return page is not null && File.Exists(uri.LocalPath) ? Save(page, Path.GetFileName(uri.LocalPath), File.ReadAllBytes(uri.LocalPath)) : source;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            ErrorLog.Write(ex, "Saving a pasted picture");
            return null;
        }

        return source;
    }

    private static string Save(string page, string name, byte[] bytes)
    {
        var folder = PageAssets.FolderFor(page);
        Directory.CreateDirectory(folder);
        var path = PageAssets.UniquePath(folder, name);
        File.WriteAllBytes(path, bytes);
        return PageAssets.RelativeUrl(page, path);
    }

    [GeneratedRegex(@"^data:image/(?<type>[a-z0-9.+-]+)(?:;[^,;]*)*?(?<base64>;base64)?,(?<data>.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DataUri();

    // Markdown that has to start a line: headings, lists, quotes, tables, fences, rules, block images.
    [GeneratedRegex(@"^(?:#{1,6} |[-+*] |\d+[.)] |> |\||```|---|!\[)")]
    private static partial Regex BlockSyntax();
}
