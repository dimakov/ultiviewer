using System.Text;
using System.Text.RegularExpressions;
using Ultiviewer.Core.Links;

namespace Ultiviewer.Core.Markdown;

public static partial class MarkdownHeadings
{
    /// <summary>GitHub-style anchor for a heading: lower case, punctuation dropped, spaces → hyphens.</summary>
    public static string Slug(string heading)
    {
        var builder = new StringBuilder(heading.Length);
        foreach (var c in heading.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_')
            {
                builder.Append(c);
            }
            else if (char.IsWhiteSpace(c))
            {
                builder.Append('-');
            }
        }

        return builder.ToString();
    }

    /// <summary>1-based line of the ATX heading whose anchor is <paramref name="fragment"/>, or null.</summary>
    public static int? FindLine(string markdown, string fragment)
    {
        var wanted = Slug(Uri.UnescapeDataString(fragment.TrimStart('#')));
        var code = CodeRanges.Find(markdown);
        var line = 1;
        var seen = new Dictionary<string, int>();
        foreach (Match match in AtxHeading().Matches(markdown))
        {
            if (CodeRanges.Contains(code, match.Index))
            {
                continue;
            }

            var slug = Slug(match.Groups["text"].Value.Trim().TrimEnd('#').Trim());
            // Duplicate headings get -1, -2… suffixes, as in the rendered ids.
            if (seen.TryGetValue(slug, out var count))
            {
                seen[slug] = count + 1;
                slug = $"{slug}-{count}";
            }
            else
            {
                seen[slug] = 1;
            }

            if (slug == wanted)
            {
                line = 1 + markdown.AsSpan(0, match.Index).Count('\n');
                return line;
            }
        }

        return null;
    }

    [GeneratedRegex(@"^[ ]{0,3}#{1,6}[ \t]+(?<text>.*?)[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex AtxHeading();
}
