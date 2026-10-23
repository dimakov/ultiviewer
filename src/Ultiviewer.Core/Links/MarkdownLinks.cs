using System.Text;
using System.Text.RegularExpressions;

namespace Ultiviewer.Core.Links;

public enum LinkSyntax
{
    /// <summary><c>[text](dest)</c> or <c>![alt](dest)</c>.</summary>
    Inline,

    /// <summary><c>[label]: dest</c>.</summary>
    Reference,

    /// <summary><c>src="dest"</c> or <c>href="dest"</c> in raw HTML.</summary>
    Html,

    /// <summary><c>[[Page title]]</c> or <c>[[Page title|label]]</c>.</summary>
    Wiki,
}

/// <summary>A link destination found in Markdown text. Start/Length cover only the destination.</summary>
public readonly record struct LinkTarget(LinkSyntax Syntax, int Start, int Length, string Raw, bool AngleBrackets)
{
    /// <summary>Destination without <c>#fragment</c>/<c>?query</c>, percent-decoded (wiki targets as written).</summary>
    public string Path => Syntax == LinkSyntax.Wiki
        ? MarkdownLinks.SplitSuffix(Raw).Path.Trim()
        : MarkdownLinks.Decode(MarkdownLinks.SplitSuffix(Raw).Path);

    public bool IsExternal => MarkdownLinks.IsExternal(Raw);
}

/// <summary>Finds and rewrites link destinations in Markdown, skipping code blocks and code spans.</summary>
public static partial class MarkdownLinks
{
    public static IReadOnlyList<LinkTarget> Find(string text)
    {
        var code = CodeRanges.Find(text);
        var result = new List<LinkTarget>();

        foreach (Match match in InlineLink().Matches(text))
        {
            Add(match, LinkSyntax.Inline);
        }

        foreach (Match match in ReferenceDefinition().Matches(text))
        {
            Add(match, LinkSyntax.Reference);
        }

        foreach (Match match in HtmlAttribute().Matches(text))
        {
            var group = match.Groups["dq"].Success ? match.Groups["dq"] : match.Groups["sq"];
            if (!CodeRanges.Contains(code, group.Index))
            {
                result.Add(new LinkTarget(LinkSyntax.Html, group.Index, group.Length, group.Value, false));
            }
        }

        foreach (Match match in WikiLink().Matches(text))
        {
            var group = match.Groups["target"];
            if (!CodeRanges.Contains(code, group.Index))
            {
                result.Add(new LinkTarget(LinkSyntax.Wiki, group.Index, group.Length, group.Value, false));
            }
        }

        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;

        void Add(Match match, LinkSyntax syntax)
        {
            var angle = match.Groups["angle"].Success;
            var group = angle ? match.Groups["angle"] : match.Groups["plain"];
            if (!CodeRanges.Contains(code, group.Index))
            {
                result.Add(new LinkTarget(syntax, group.Index, group.Length, group.Value, angle));
            }
        }
    }

    /// <summary>
    /// Replaces link destinations. <paramref name="map"/> receives each link and its decoded path and returns
    /// the new decoded path (keeping any #fragment), or null to leave the link unchanged.
    /// </summary>
    public static string Rewrite(string text, Func<LinkTarget, string, string?> map)
    {
        var links = Find(text);
        var builder = new StringBuilder(text);
        for (var i = links.Count - 1; i >= 0; i--)
        {
            var link = links[i];
            if (link.IsExternal)
            {
                continue;
            }

            if (map(link, link.Path) is not { } newPath)
            {
                continue;
            }

            var suffix = SplitSuffix(link.Raw).Suffix;
            var replacement = link.Syntax switch
            {
                LinkSyntax.Wiki => newPath + suffix,
                _ when link.AngleBrackets => newPath + suffix,
                LinkSyntax.Html => newPath.Replace("\"", "%22").Replace("'", "%27") + suffix,
                _ => Encode(newPath) + suffix,
            };
            builder.Remove(link.Start, link.Length).Insert(link.Start, replacement);
        }

        return builder.ToString();
    }

    /// <summary>Encodes a relative path for a Markdown link destination (spaces, parentheses, percent).</summary>
    public static string Encode(string path)
    {
        var builder = new StringBuilder(path.Length + 8);
        foreach (var c in path)
        {
            builder.Append(c switch
            {
                ' ' => "%20",
                '%' => "%25",
                '(' => "%28",
                ')' => "%29",
                '<' => "%3C",
                '>' => "%3E",
                '\\' => "/",
                _ => c.ToString(),
            });
        }

        return builder.ToString();
    }

    public static string Decode(string raw)
    {
        try
        {
            return Uri.UnescapeDataString(raw);
        }
        catch (UriFormatException)
        {
            return raw;
        }
    }

    public static (string Path, string Suffix) SplitSuffix(string raw)
    {
        var index = raw.IndexOfAny(['#', '?']);
        return index < 0 ? (raw, "") : (raw[..index], raw[index..]);
    }

    public static bool IsExternal(string raw) =>
        UriScheme().IsMatch(raw) || raw.StartsWith("//", StringComparison.Ordinal);

    // ](dest "title") or ](<dest>) — destination may contain one level of balanced parentheses.
    [GeneratedRegex(@"(?<=\]\()[ \t]*(?:<(?<angle>[^<>\r\n]*)>|(?<plain>(?:[^\s()\\]|\\.|\((?:[^\s()\\]|\\.)*\))+))")]
    private static partial Regex InlineLink();

    [GeneratedRegex(@"^[ ]{0,3}\[(?!\[)[^\]\r\n]+\]:[ \t]*(?:<(?<angle>[^<>\r\n]*)>|(?<plain>\S+))", RegexOptions.Multiline)]
    private static partial Regex ReferenceDefinition();

    [GeneratedRegex(@"\b(?:src|href)[ \t]*=[ \t]*(?:""(?<dq>[^""\r\n]*)""|'(?<sq>[^'\r\n]*)')", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlAttribute();

    [GeneratedRegex(@"\[\[(?<target>[^\[\]\|\r\n]+)(?:\|[^\[\]\r\n]*)?\]\]")]
    private static partial Regex WikiLink();

    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.\-]*:")]
    private static partial Regex UriScheme();
}
