using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Ultiviewer.Core.Notebooks;

namespace Ultiviewer.Core.Clipping;

/// <summary>An image of a clipped page: where it comes from, where it goes and the link the page uses for it.</summary>
public sealed record ImageDownload(Uri Source, string TargetPath, string Link);

/// <summary>A clipped page's Markdown and the images to download into its attachment folder.</summary>
public sealed record ClipResult(string Markdown, IReadOnlyList<ImageDownload> Images);

/// <summary>
/// The readable part of a web page, for "Clip web page…": the title (<c>og:title</c>, <c>&lt;title&gt;</c> or the
/// first heading), byline and site name, and the article body. The body is the <c>&lt;article&gt;</c> (or
/// <c>&lt;main&gt;</c>) with the most text, otherwise the element whose paragraphs hold the most text; navigation,
/// headers, footers, sidebars, comments, share buttons and forms are removed first.
/// </summary>
public sealed partial class WebArticle
{
    private const int MaxImages = 60;

    private readonly IElement _content;

    private WebArticle(Uri url, string title, string? byline, string? siteName, IElement content)
    {
        Url = url;
        Title = title;
        Byline = byline;
        SiteName = siteName;
        _content = content;
    }

    public Uri Url { get; }

    public string Title { get; }

    public string? Byline { get; }

    public string? SiteName { get; }

    public static WebArticle Parse(string html, Uri url) => FromDocument(new HtmlParser().ParseDocument(html), url);

    /// <summary>Parses raw bytes: the charset from the HTTP header if known, otherwise a BOM or <c>&lt;meta charset&gt;</c>.</summary>
    public static WebArticle Parse(byte[] html, string? charset, Uri url)
    {
        Encoding? encoding = null;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                encoding = Encoding.GetEncoding(charset.Trim('"', '\'', ' '));
            }
            catch (ArgumentException)
            {
            }
        }

        var parser = new HtmlParser();
        var document = encoding is not null ? parser.ParseDocument(encoding.GetString(html)) : parser.ParseDocument(new MemoryStream(html));
        return FromDocument(document, url);
    }

    private static WebArticle FromDocument(IHtmlDocument document, Uri url)
    {
        string? Meta(params string[] names) => names
            .Select(n => document.QuerySelector($"meta[property='{n}'], meta[name='{n}']")?.GetAttribute("content")?.Trim())
            .FirstOrDefault(v => !string.IsNullOrEmpty(v));

        var siteName = Meta("og:site_name", "application-name");
        var title = Meta("og:title", "twitter:title") ?? document.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            title = document.QuerySelector("h1")?.TextContent.Trim();
        }

        title = Collapse(title ?? "");
        if (siteName is not null && (title.EndsWith(" - " + siteName, StringComparison.OrdinalIgnoreCase) || title.EndsWith(" | " + siteName, StringComparison.OrdinalIgnoreCase)))
        {
            title = title[..^(siteName.Length + 3)].Trim();
        }

        var byline = Meta("author", "article:author", "twitter:creator") ?? document.QuerySelector("[rel=author], [itemprop=author], .byline, .author")?.TextContent;
        byline = byline is null ? null : Collapse(byline);
        if (byline is { Length: > 80 } || byline?.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true)
        {
            byline = null;
        }

        var body = document.Body ?? document.DocumentElement;
        RemoveClutter(body);
        var content = PickContent(body);
        return new WebArticle(url, title.Length > 0 ? title : url.Host, byline, siteName, content);
    }

    /// <summary>
    /// The page as Markdown for a new page at <paramref name="pagePath"/>: front matter (source, clipped,
    /// author, site), the title as a heading, the article, and its images as attachments.
    /// </summary>
    public ClipResult ToMarkdown(string pagePath, DateTime now)
    {
        var folder = PageAssets.FolderFor(pagePath);
        var images = new List<ImageDownload>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var converter = new HtmlToMarkdown(Url, (source, _) =>
        {
            if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "file"))
            {
                return null; // inline data and unknown schemes are dropped
            }

            if (images.FirstOrDefault(i => i.Source == uri) is { } known)
            {
                return known.Link;
            }

            if (images.Count >= MaxImages)
            {
                return uri.AbsoluteUri;
            }

            var target = Path.Combine(folder, UniqueName(ImageName(uri, images.Count + 1), names));
            var link = PageAssets.RelativeUrl(pagePath, target);
            images.Add(new ImageDownload(uri, target, link));
            return link;
        });

        var content = _content.Clone(deep: true) as IElement ?? _content;
        if (content.QuerySelector("h1") is { } heading && Collapse(heading.TextContent).Equals(Title, StringComparison.OrdinalIgnoreCase))
        {
            heading.Remove(); // the page gets the title as its own heading
        }

        var markdown = converter.Convert(content).Markdown;
        var front = new StringBuilder("---\n");
        front.Append("source: ").Append(Url.AbsoluteUri).Append('\n');
        front.Append("clipped: ").Append(now.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        if (Byline is not null)
        {
            front.Append("author: ").Append(Yaml(Byline)).Append('\n');
        }

        if (SiteName is not null)
        {
            front.Append("site: ").Append(Yaml(SiteName)).Append('\n');
        }

        front.Append("---\n\n# ").Append(Title).Append("\n\n").Append(markdown).Append('\n');
        return new ClipResult(front.ToString(), images);
    }

    private static void RemoveClutter(IElement body)
    {
        foreach (var element in body.QuerySelectorAll(
            "script, style, noscript, template, iframe, form, nav, aside, button, svg, dialog, "
            + "[role=navigation], [role=banner], [role=contentinfo], [role=complementary], [role=dialog], [aria-hidden=true], [hidden]").ToList())
        {
            element.Remove();
        }

        foreach (var element in body.QuerySelectorAll("header, footer").Where(e => e.Closest("article") is null).ToList())
        {
            element.Remove(); // the site's header and footer, not an article's own
        }

        foreach (var element in body.QuerySelectorAll("[class], [id]").ToList())
        {
            var names = (element.ClassName ?? "") + " " + element.Id;
            if (element.LocalName is not ("body" or "article" or "main") && Clutter().IsMatch(names) && !Content().IsMatch(names)
                && element.QuerySelectorAll("p").Length < 3)
            {
                element.Remove();
            }
        }
    }

    private static IElement PickContent(IElement body)
    {
        var landmarks = body.QuerySelectorAll("article, main, [role=main], [itemprop=articleBody]").ToList();
        if (landmarks.Count > 0)
        {
            var best = landmarks.MaxBy(e => TextLength(e))!;
            if (TextLength(best) > 200)
            {
                return best;
            }
        }

        // Score parents by the text of their paragraphs (half for grandparents), like Readability.
        var scores = new Dictionary<IElement, double>();
        foreach (var paragraph in body.QuerySelectorAll("p, pre, td"))
        {
            var length = Collapse(paragraph.TextContent).Length;
            if (length < 25)
            {
                continue;
            }

            var score = 1 + Math.Min(3, length / 100.0) + paragraph.TextContent.Count(c => c == ',');
            if (paragraph.ParentElement is { } parent)
            {
                scores[parent] = scores.GetValueOrDefault(parent) + score;
                if (parent.ParentElement is { } grandparent)
                {
                    scores[grandparent] = scores.GetValueOrDefault(grandparent) + score / 2;
                }
            }
        }

        return scores.Count == 0 ? body : scores.MaxBy(s => s.Value * (1 - LinkDensity(s.Key))).Key;
    }

    private static int TextLength(IElement element) => Collapse(element.TextContent).Length;

    private static double LinkDensity(IElement element)
    {
        var total = Math.Max(1, TextLength(element));
        return element.QuerySelectorAll("a").Sum(a => Collapse(a.TextContent).Length) / (double)total;
    }

    private static string ImageName(Uri uri, int number)
    {
        var name = uri.IsFile ? Path.GetFileName(uri.LocalPath) : Uri.UnescapeDataString(uri.Segments.LastOrDefault() ?? "").Trim('/');
        var extension = Path.GetExtension(name).ToLowerInvariant();
        var stem = FileNameRules.MakeValid(Path.GetFileNameWithoutExtension(name), "image");
        if (stem.Length > 60)
        {
            stem = stem[..60].TrimEnd(' ', '.', '-');
        }

        if (!PageAssets.IsImage("x" + extension))
        {
            extension = ".jpg"; // pictures without an extension (CDN URLs); viewers sniff the real type
        }

        return (stem is "image" or "" ? $"image-{number}" : stem) + extension;
    }

    private static string UniqueName(string name, HashSet<string> used)
    {
        var candidate = name;
        for (var n = 2; !used.Add(candidate); n++)
        {
            candidate = $"{Path.GetFileNameWithoutExtension(name)} {n}{Path.GetExtension(name)}";
        }

        return candidate;
    }

    private static string Yaml(string value) =>
        value.IndexOfAny([':', '#', '\'', '"', '[', ']', '{', '}', ',', '&', '*', '!', '|', '>', '%', '@', '`']) >= 0 ? "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"" : value;

    private static string Collapse(string text) => Whitespace().Replace(text, " ").Trim();

    [GeneratedRegex(@"[\s ]+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"comment|share|social|promo|related|footer|navbar|menu|sidebar|cookie|subscribe|newsletter|advert|\bads?\b|banner|breadcrumb|popup|modal|signup|login|recommend", RegexOptions.IgnoreCase)]
    private static partial Regex Clutter();

    [GeneratedRegex(@"article|content|post|entry|story|body|main", RegexOptions.IgnoreCase)]
    private static partial Regex Content();
}
