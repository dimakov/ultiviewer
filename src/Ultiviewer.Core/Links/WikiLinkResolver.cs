namespace Ultiviewer.Core.Links;

/// <summary>Resolves <c>[[title]]</c> or <c>[[Section/title]]</c> to a page file.</summary>
public static class WikiLinkResolver
{
    /// <summary>
    /// Picks the best page for <paramref name="target"/> among <paramref name="pages"/> (full .md paths):
    /// a page next to <paramref name="fromPath"/> wins, then pages in the same notebook, then the shortest path.
    /// </summary>
    public static string? Resolve(string target, string? fromPath, IEnumerable<string> pages, string? notebookRoot = null)
    {
        var normalized = target.Replace('\\', '/').Trim().Trim('/');
        if (normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^3];
        }

        if (normalized.Length == 0)
        {
            return null;
        }

        var fromFolder = fromPath is null ? null : Path.GetDirectoryName(fromPath);
        return pages
            .Where(page => Matches(page, normalized))
            .OrderBy(page => fromFolder is not null && string.Equals(Path.GetDirectoryName(page), fromFolder, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(page => notebookRoot is not null && page.StartsWith(notebookRoot, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(page => page.Length)
            .FirstOrDefault();
    }

    private static bool Matches(string page, string target)
    {
        var withoutExtension = page.Replace('\\', '/');
        if (withoutExtension.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            withoutExtension = withoutExtension[..^3];
        }

        return withoutExtension.Equals(target, StringComparison.OrdinalIgnoreCase)
            || withoutExtension.EndsWith("/" + target, StringComparison.OrdinalIgnoreCase);
    }
}
