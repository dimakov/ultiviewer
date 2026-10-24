using System.Text.RegularExpressions;

namespace Ultiviewer.Core.Links;

public enum LinkState
{
    /// <summary>Leads to an existing page or file.</summary>
    Ok,

    /// <summary>Points to a page or file that doesn't exist.</summary>
    Broken,

    /// <summary>A web or mail address.</summary>
    External,
}

/// <summary>A link in a page: where it is, what it shows, and what it leads to.</summary>
public sealed record OutgoingLink(int Line, string Target, string? ResolvedPath, LinkState State, bool IsWiki);

/// <summary>A line of another page that links to (or mentions) a page.</summary>
public sealed record LinkLine(int Line, string Text);

/// <summary>
/// Links between pages: a page's outgoing links (resolved, broken ones marked), the lines of another page that
/// link to it (wiki links by title, Markdown links by relative path), and unlinked mentions of its title.
/// </summary>
public static partial class PageLinks
{
    public static IReadOnlyList<OutgoingLink> Outgoing(string text, string pagePath, IReadOnlyCollection<string> allPages, string? notebookRoot)
    {
        var result = new List<OutgoingLink>();
        var lineStarts = LineStarts(text);
        foreach (var link in MarkdownLinks.Find(text))
        {
            var line = LineOf(lineStarts, link.Start);
            if (link.Syntax == LinkSyntax.Wiki)
            {
                var target = link.Path;
                if (target.Length == 0)
                {
                    continue; // [[#heading]] inside the page
                }

                var resolved = WikiLinkResolver.Resolve(target, pagePath, allPages, notebookRoot);
                result.Add(new OutgoingLink(line, target, resolved, resolved is null ? LinkState.Broken : LinkState.Ok, IsWiki: true));
            }
            else if (link.IsExternal)
            {
                result.Add(new OutgoingLink(line, link.Raw, null, LinkState.External, IsWiki: false));
            }
            else if (link.Path.Length > 0 && Resolve(pagePath, link.Path) is { } full)
            {
                result.Add(new OutgoingLink(line, link.Path, full, File.Exists(full) || Directory.Exists(full) ? LinkState.Ok : LinkState.Broken, IsWiki: false));
            }
        }

        return result;
    }

    /// <summary>Lines of <paramref name="text"/> (the page at <paramref name="fromPath"/>) that link to <paramref name="targetPath"/>.</summary>
    public static IReadOnlyList<LinkLine> LinksTo(string text, string fromPath, string targetPath, IReadOnlyCollection<string> allPages, string? notebookRoot)
    {
        var lines = text.Split('\n');
        var lineStarts = LineStarts(text);
        var found = new SortedSet<int>();
        foreach (var link in MarkdownLinks.Find(text))
        {
            string? resolved = link.Syntax == LinkSyntax.Wiki
                ? link.Path.Length > 0 ? WikiLinkResolver.Resolve(link.Path, fromPath, allPages, notebookRoot) : null
                : link.IsExternal || link.Path.Length == 0 ? null : Resolve(fromPath, link.Path);
            if (resolved is not null && string.Equals(Path.GetFullPath(resolved), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
            {
                found.Add(LineOf(lineStarts, link.Start));
            }
        }

        return [.. found.Select(n => new LinkLine(n, lines[n - 1].TrimEnd('\r').Trim()))];
    }

    /// <summary>Lines that mention <paramref name="title"/> as a whole phrase outside links, code and headings' markers.</summary>
    public static IReadOnlyList<LinkLine> Mentions(string text, string title)
    {
        if (title.Trim().Length < 3)
        {
            return [];
        }

        var linked = LinkSpan().Matches(text).Select(m => (m.Index, m.Index + m.Length)).ToList();
        var code = CodeRanges.Find(text);
        var lines = text.Split('\n');
        var lineStarts = LineStarts(text);
        var pattern = new Regex(@"(?<![\p{L}\p{N}_])" + Regex.Escape(title.Trim()) + @"(?![\p{L}\p{N}_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var found = new SortedSet<int>();
        foreach (Match match in pattern.Matches(text))
        {
            if (CodeRanges.Contains(code, match.Index) || linked.Any(l => match.Index >= l.Item1 && match.Index < l.Item2))
            {
                continue;
            }

            found.Add(LineOf(lineStarts, match.Index));
        }

        return [.. found.Select(n => new LinkLine(n, lines[n - 1].TrimEnd('\r').Trim()))];
    }

    /// <summary>Turns the first plain mention of <paramref name="title"/> on a 1-based line into <c>[[title]]</c>.</summary>
    public static string LinkMention(string text, int line, string title)
    {
        var lineStarts = LineStarts(text);
        if (line < 1 || line > lineStarts.Count)
        {
            return text;
        }

        var start = lineStarts[line - 1];
        var end = line < lineStarts.Count ? lineStarts[line] : text.Length;
        var pattern = new Regex(@"(?<![\p{L}\p{N}_\[])" + Regex.Escape(title.Trim()) + @"(?![\p{L}\p{N}_\]])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var match = pattern.Match(text, start, end - start);
        if (!match.Success)
        {
            return text;
        }

        var replacement = match.Value == title ? $"[[{title}]]" : $"[[{title}|{match.Value}]]";
        return text[..match.Index] + replacement + text[(match.Index + match.Length)..];
    }

    private static string? Resolve(string fromPath, string relative)
    {
        try
        {
            var folder = Path.GetDirectoryName(fromPath) ?? "";
            return Path.GetFullPath(Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts;
    }

    /// <summary>1-based line of an offset.</summary>
    private static int LineOf(List<int> starts, int offset)
    {
        var index = starts.BinarySearch(offset);
        return (index >= 0 ? index : ~index - 1) + 1;
    }

    /// <summary>Whole links: <c>[[…]]</c>, <c>[text](dest)</c>, <c>&lt;url&gt;</c>.</summary>
    [GeneratedRegex(@"!?\[\[[^\]\n]*\]\]|!?\[[^\]\n]*\]\([^)\n]*\)|<[a-z]+://[^>\s]*>", RegexOptions.IgnoreCase)]
    private static partial Regex LinkSpan();
}
