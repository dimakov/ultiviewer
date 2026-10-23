namespace Ultiviewer.Core.Search;

/// <summary>
/// Subsequence matching for "go to page" (Ctrl+E): every pattern character must appear in order; consecutive
/// characters, word starts and an exact substring score higher. Case- and accent-insensitive.
/// </summary>
public static class FuzzyMatcher
{
    /// <summary>Score (higher is better) or null if <paramref name="pattern"/> doesn't match.</summary>
    public static int? Score(string pattern, string candidate)
    {
        var p = TextFolding.Fold(pattern.Trim());
        if (p.Length == 0)
        {
            return 0;
        }

        var c = TextFolding.Fold(candidate);
        var score = 0;
        var position = 0;
        var previous = -2;
        foreach (var ch in p)
        {
            if (ch == ' ')
            {
                continue;
            }

            var found = c.IndexOf(ch, position);
            if (found < 0)
            {
                return null;
            }

            score += 10;
            if (found == previous + 1)
            {
                score += 15;
            }

            if (found == 0 || !char.IsLetterOrDigit(c[found - 1]))
            {
                score += 20;
            }

            previous = found;
            position = found + 1;
        }

        var substring = c.IndexOf(p.Replace(" ", ""), StringComparison.Ordinal);
        if (substring >= 0)
        {
            score += 50 + (substring == 0 ? 50 : 0);
        }

        // Shorter candidates are better matches for the same pattern.
        return score - Math.Min(c.Length, 60) / 4;
    }
}
