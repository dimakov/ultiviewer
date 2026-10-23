using System.Text.RegularExpressions;

namespace Ultiviewer.Core.Text;

public sealed record FindOptions(bool MatchCase = false, bool WholeWord = false, bool UseRegex = false);

public readonly record struct TextRange(int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>Find and replace for the editor's find bar and replace-in-files (literal or .NET regex).</summary>
public static class TextSearcher
{
    public const int MaxMatches = 20000;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    /// <summary>Builds the regex for a find pattern. Throws <see cref="ArgumentException"/> for an invalid regex.</summary>
    public static Regex Build(string pattern, FindOptions options)
    {
        var body = options.UseRegex ? pattern : Regex.Escape(pattern);
        if (options.WholeWord)
        {
            body = $@"(?<![\w]){(options.UseRegex ? "(?:" + body + ")" : body)}(?![\w])";
        }

        var flags = RegexOptions.Multiline | RegexOptions.CultureInvariant;
        if (!options.MatchCase)
        {
            flags |= RegexOptions.IgnoreCase;
        }

        return new Regex(body, flags, Timeout);
    }

    /// <summary>Matches in <paramref name="text"/> between <paramref name="start"/> and <paramref name="end"/>.</summary>
    public static IReadOnlyList<TextRange> FindAll(string text, Regex regex, int start = 0, int end = -1, int max = MaxMatches)
    {
        if (end < 0 || end > text.Length)
        {
            end = text.Length;
        }

        var result = new List<TextRange>();
        try
        {
            // Match(text, startat) keeps the surrounding text visible to ^, \b and lookbehinds.
            var position = Math.Clamp(start, 0, text.Length);
            while (position <= end && result.Count < max)
            {
                var match = regex.Match(text, position);
                if (!match.Success || match.Index + match.Length > end)
                {
                    break;
                }

                if (match.Length == 0)
                {
                    position = match.Index + 1; // empty matches are skipped, never returned
                    continue;
                }

                result.Add(new TextRange(match.Index, match.Length));
                position = match.Index + match.Length;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Return what was found so far.
        }

        return result;
    }

    /// <summary>The text that replaces the match at <paramref name="range"/> (expands $1, ${name} for regexes).</summary>
    public static string ReplacementFor(string text, TextRange range, Regex regex, string replacement, FindOptions options)
    {
        if (!options.UseRegex)
        {
            return replacement;
        }

        var match = regex.Match(text, range.Start);
        return match.Success && match.Index == range.Start ? match.Result(replacement) : replacement;
    }

    /// <summary>Replaces every match and returns the new text and the number of replacements.</summary>
    public static (string Text, int Count) ReplaceAll(string text, Regex regex, string replacement, FindOptions options)
    {
        var count = 0;
        var result = regex.Replace(text, match =>
        {
            if (match.Length == 0)
            {
                return match.Value;
            }

            count++;
            return options.UseRegex ? match.Result(replacement) : replacement;
        });
        return (result, count);
    }
}
