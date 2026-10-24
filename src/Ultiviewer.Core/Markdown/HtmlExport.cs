using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Ultiviewer.Core.Markdown;

/// <summary>A page to export: its title, Markdown and folder (for relative images).</summary>
public sealed record ExportPage(string Title, string Markdown, string? Folder);

/// <summary>
/// Standalone HTML export: pages are rendered with <see cref="MarkdownRenderer"/>, local images become data URIs,
/// page breaks become print breaks and wiki links point inside the document. The app renders the result once in
/// the preview (math, diagrams, code colors) and then saves it, prints it or turns it into a PDF.
/// </summary>
public static partial class HtmlExport
{
    /// <summary>
    /// HTML for one or more pages. Each page becomes a <c>&lt;section&gt;</c> with an anchor; a page without its
    /// own first-level heading gets its title as one when <paramref name="titlePages"/> is set.
    /// </summary>
    public static string Compose(IReadOnlyList<ExportPage> pages, bool titlePages, bool pageBreakBetween)
    {
        var anchors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in pages)
        {
            anchors.TryAdd(page.Title, "page-" + MarkdownHeadings.Slug(page.Title));
        }

        var builder = new StringBuilder();
        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            var html = MarkdownRenderer.ToHtml(page.Markdown);
            html = InlineImages(html, page.Folder);
            html = PageBreaks(html);
            html = ResolveLinks(html, anchors);
            var startsWithTitle = FirstHeading().IsMatch(html);
            builder.Append("<section class=\"export-page\" id=\"").Append(anchors[page.Title]).Append("\">");
            if (titlePages && !startsWithTitle)
            {
                builder.Append("<h1>").Append(WebUtility.HtmlEncode(page.Title)).Append("</h1>");
            }

            builder.Append(html).Append("</section>");
            if (pageBreakBetween && i < pages.Count - 1)
            {
                builder.Append("<div class=\"page-break\"></div>");
            }
        }

        return builder.ToString();
    }

    /// <summary>Local images (relative to <paramref name="folder"/>, or absolute paths) become data URIs; web images stay as they are.</summary>
    public static string InlineImages(string html, string? folder) => ImageSource().Replace(html, match =>
    {
        var source = WebUtility.HtmlDecode(match.Groups["src"].Value);
        if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            return match.Value;
        }

        try
        {
            var path = Uri.UnescapeDataString(source.Split('?', '#')[0]).Replace('/', Path.DirectorySeparatorChar);
            if (!Path.IsPathRooted(path))
            {
                if (folder is null)
                {
                    return match.Value;
                }

                path = Path.GetFullPath(Path.Combine(folder, path));
            }

            if (!File.Exists(path) || new FileInfo(path).Length > 25 * 1024 * 1024)
            {
                return match.Value;
            }

            var data = "data:" + MediaType(path) + ";base64," + Convert.ToBase64String(File.ReadAllBytes(path));
            return match.Value.Replace(match.Groups["src"].Value, data, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return match.Value;
        }
    });

    /// <summary><c>&lt;!-- pagebreak --&gt;</c>, and paragraphs that are just <c>\pagebreak</c> or <c>\newpage</c>, start a new printed page.</summary>
    public static string PageBreaks(string html) => PageBreak().Replace(html, "<div class=\"page-break\"></div>");

    /// <summary>
    /// Wiki links to exported pages jump to that page's section; other wiki links and #tags become plain text
    /// (they mean nothing outside the app).
    /// </summary>
    public static string ResolveLinks(string html, IReadOnlyDictionary<string, string> anchors) => AppLink().Replace(html, match =>
    {
        var target = Uri.UnescapeDataString(match.Groups["target"].Value);
        var inner = match.Groups["inner"].Value;
        if (match.Groups["scheme"].Value == "wiki")
        {
            var page = target.Split('#')[0].Trim();
            if (anchors.TryGetValue(page, out var anchor))
            {
                return $"<a href=\"#{anchor}\" class=\"wikilink\">{inner}</a>";
            }

            return $"<span class=\"wikilink\">{inner}</span>";
        }

        return $"<span class=\"tag\">{inner}</span>";
    });

    /// <summary>A complete HTML file: the given styles inline, the body in the preview's layout.</summary>
    public static string Document(string title, string bodyHtml, IEnumerable<string> styles)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<!doctype html>");
        builder.AppendLine("<html lang=\"en\">");
        builder.AppendLine("<head>");
        builder.AppendLine("<meta charset=\"utf-8\">");
        builder.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        builder.Append("<title>").Append(WebUtility.HtmlEncode(title)).AppendLine("</title>");
        foreach (var style in styles)
        {
            builder.AppendLine("<style>").AppendLine(style).AppendLine("</style>");
        }

        builder.AppendLine("</head>");
        builder.AppendLine("<body>");
        builder.Append("<main id=\"content\" class=\"markdown-body\">").Append(bodyHtml).AppendLine("</main>");
        builder.AppendLine("</body>");
        builder.AppendLine("</html>");
        return builder.ToString();
    }

    /// <summary>Embeds the woff2 fonts a stylesheet references (KaTeX's), dropping the other formats.</summary>
    public static string EmbedFonts(string css, string cssFolder) => FontSource().Replace(css, match =>
    {
        var path = Path.Combine(cssFolder, match.Groups["file"].Value.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path)
            ? $"src:url(data:font/woff2;base64,{Convert.ToBase64String(File.ReadAllBytes(path))}) format(\"woff2\")"
            : match.Value;
    });

    private static string MediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".bmp" => "image/bmp",
        ".ico" => "image/x-icon",
        _ => "application/octet-stream",
    };

    [GeneratedRegex(@"<img\b[^>]*?\bsrc=""(?<src>[^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex ImageSource();

    [GeneratedRegex(@"<!--\s*page-?break\s*-->|<p\b[^>]*>\s*\\(?:pagebreak|newpage)\s*</p>", RegexOptions.IgnoreCase)]
    private static partial Regex PageBreak();

    [GeneratedRegex(@"<a\b[^>]*?href=""(?<scheme>wiki|tag):(?<target>[^""]*)""[^>]*>(?<inner>.*?)</a>", RegexOptions.Singleline)]
    private static partial Regex AppLink();

    [GeneratedRegex(@"^\s*<h1\b")]
    private static partial Regex FirstHeading();

    [GeneratedRegex(@"src:url\((?<file>[^)]+?\.woff2)\)\s*format\(""woff2""\)(?:\s*,\s*url\([^)]*\)\s*format\(""[^""]*""\))*")]
    private static partial Regex FontSource();
}
