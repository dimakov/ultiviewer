using Ultiviewer.Core.Links;

namespace Ultiviewer.Core.Notebooks;

/// <summary>Attachments of a page: pasted images and dropped files go to <c>Title.assets/</c> next to it.</summary>
public static class PageAssets
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".bmp", ".avif",
    };

    public static string FolderFor(string pagePath) =>
        Path.Combine(Path.GetDirectoryName(pagePath)!, Path.GetFileNameWithoutExtension(pagePath) + NotebookScanner.AssetsSuffix);

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    public static string ScreenshotName(DateTime now) => $"{now:yyyyMMdd-HHmmss}.png";

    /// <summary><paramref name="fileName"/>, or "name 2.ext", "name 3.ext"… when taken.</summary>
    public static string UniquePath(string folder, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var candidate = Path.Combine(folder, fileName);
        for (var n = 2; File.Exists(candidate) || Directory.Exists(candidate); n++)
        {
            candidate = Path.Combine(folder, $"{stem} {n}{extension}");
        }

        return candidate;
    }

    /// <summary>Markdown for a link from the page to an attachment: image syntax for images.</summary>
    public static string MarkdownLink(string pagePath, string assetPath)
    {
        var relative = RelativeUrl(pagePath, assetPath);
        var name = Path.GetFileNameWithoutExtension(assetPath);
        return IsImage(assetPath)
            ? $"![{name}]({relative})"
            : $"[{Path.GetFileName(assetPath)}]({relative})";
    }

    /// <summary>The encoded link target from the page to an attachment ("Title.assets/image%201.png").</summary>
    public static string RelativeUrl(string pagePath, string assetPath) =>
        MarkdownLinks.Encode(Path.GetRelativePath(Path.GetDirectoryName(pagePath)!, assetPath).Replace(Path.DirectorySeparatorChar, '/'));
}

/// <summary>New pages from templates in a notebook's <c>_templates</c> folder.</summary>
public static class PageTemplates
{
    public const string FolderName = "_templates";

    public static IReadOnlyList<string> List(string notebookRoot)
    {
        var folder = Path.Combine(notebookRoot, FolderName);
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*" + NotebookScanner.PageExtension).Order(StringComparer.CurrentCultureIgnoreCase).ToList()
            : [];
    }

    /// <summary>
    /// Fills <c>{{title}}</c>, <c>{{date}}</c>, <c>{{time}}</c>, <c>{{datetime}}</c> and, as in Obsidian,
    /// <c>{{date:dddd, d MMMM yyyy}}</c> with any .NET date format.
    /// </summary>
    public static string Apply(string template, string title, DateTime now, string dateFormat, string timeFormat) =>
        System.Text.RegularExpressions.Regex.Replace(template, @"\{\{date:(?<format>[^}]+)\}\}", m =>
            {
                try
                {
                    return now.ToString(m.Groups["format"].Value, System.Globalization.CultureInfo.CurrentCulture);
                }
                catch (FormatException)
                {
                    return m.Value;
                }
            }, System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Replace("{{title}}", title, StringComparison.OrdinalIgnoreCase)
            .Replace("{{datetime}}", now.ToString(dateFormat + " " + timeFormat), StringComparison.OrdinalIgnoreCase)
            .Replace("{{date}}", now.ToString(dateFormat), StringComparison.OrdinalIgnoreCase)
            .Replace("{{time}}", now.ToString(timeFormat), StringComparison.OrdinalIgnoreCase);
}
