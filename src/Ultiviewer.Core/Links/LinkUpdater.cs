namespace Ultiviewer.Core.Links;

/// <summary>A path (file or folder) that moved: everything at or below <see cref="Old"/> is now below <see cref="New"/>.</summary>
public sealed record PathMove(string Old, string New);

/// <summary>
/// Keeps Markdown links working after pages or sections are renamed or moved:
/// <list type="bullet">
/// <item>links in any page that point at a moved file or folder are re-pointed;</item>
/// <item>pages that moved themselves get their own relative links rebased onto their new folder;</item>
/// <item><c>[[Old title]]</c> wiki links follow a page rename.</item>
/// </list>
/// </summary>
public static class LinkUpdater
{
    /// <summary>
    /// Rewrites <paramref name="text"/> of the page now at <paramref name="pagePath"/>. Returns the new text and
    /// the number of changed links (0 means unchanged).
    /// </summary>
    public static (string Text, int Count) Rewrite(
        string text,
        string pagePath,
        IReadOnlyList<PathMove> moves,
        string? oldTitle = null,
        string? newTitle = null)
    {
        var oldPagePath = Unmap(pagePath, moves);
        var oldFolder = Path.GetDirectoryName(oldPagePath)!;
        var newFolder = Path.GetDirectoryName(pagePath)!;
        var count = 0;

        var result = MarkdownLinks.Rewrite(text, (link, decoded) =>
        {
            if (link.Syntax == LinkSyntax.Wiki)
            {
                if (oldTitle is null || newTitle is null || string.Equals(oldTitle, newTitle, StringComparison.Ordinal))
                {
                    return null;
                }

                var target = decoded.Replace('\\', '/');
                var slash = target.LastIndexOf('/');
                var titlePart = slash < 0 ? target : target[(slash + 1)..];
                if (!string.Equals(titlePart, oldTitle, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                count++;
                return (slash < 0 ? "" : target[..(slash + 1)]) + newTitle;
            }

            if (decoded.Length == 0 || Path.IsPathRooted(decoded))
            {
                return null;
            }

            string absolute;
            try
            {
                absolute = Path.GetFullPath(Path.Combine(oldFolder, decoded.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }

            var mapped = Map(absolute, moves);
            var relative = Path.GetRelativePath(newFolder, mapped).Replace(Path.DirectorySeparatorChar, '/');
            if (relative == Normalize(decoded))
            {
                return null;
            }

            // Leave links alone when neither the page nor the target moved.
            if (string.Equals(mapped, absolute, StringComparison.OrdinalIgnoreCase) && string.Equals(oldFolder, newFolder, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            count++;
            return relative;
        });

        return (result, count);
    }

    /// <summary>Where <paramref name="path"/> is after all moves, applied in order (a folder move, then a rename inside it).</summary>
    public static string Map(string path, IReadOnlyList<PathMove> moves)
    {
        foreach (var move in moves)
        {
            if (TryRebase(path, move.Old, move.New, out var rebased))
            {
                path = rebased;
            }
        }

        return path;
    }

    /// <summary>Where <paramref name="path"/> was before the moves (they are undone in reverse order).</summary>
    public static string Unmap(string path, IReadOnlyList<PathMove> moves)
    {
        for (var i = moves.Count - 1; i >= 0; i--)
        {
            if (TryRebase(path, moves[i].New, moves[i].Old, out var rebased))
            {
                path = rebased;
            }
        }

        return path;
    }

    private static bool TryRebase(string path, string from, string to, out string rebased)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(from));
        if (full.Equals(prefix, StringComparison.OrdinalIgnoreCase))
        {
            rebased = Path.GetFullPath(to);
            return true;
        }

        if (full.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            rebased = Path.Combine(Path.GetFullPath(to), full[(prefix.Length + 1)..]);
            return true;
        }

        rebased = path;
        return false;
    }

    private static string Normalize(string relative)
    {
        var parts = new List<string>();
        foreach (var part in relative.Replace('\\', '/').Split('/'))
        {
            if (part is "" or ".")
            {
                continue;
            }

            if (part == ".." && parts.Count > 0 && parts[^1] != "..")
            {
                parts.RemoveAt(parts.Count - 1);
            }
            else
            {
                parts.Add(part);
            }
        }

        return string.Join('/', parts);
    }
}
