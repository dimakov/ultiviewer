namespace Ultiviewer.Core.Search;

/// <summary>
/// Orders command palette entries. With an empty query, recently run commands come first and the rest keep their
/// menu order; otherwise titles are fuzzy-matched (a match that needs the menu path counts half) and recent use
/// adds a bonus.
/// </summary>
public static class CommandRanking
{
    public const int MaxRecent = 20;

    public static IReadOnlyList<T> Rank<T>(IEnumerable<T> items, string query, IReadOnlyList<string> recent,
        Func<T, string> key, Func<T, string> title, Func<T, string> location)
    {
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < recent.Count; i++)
        {
            rank.TryAdd(recent[i], i);
        }

        var indexed = items.Select((item, index) => (Item: item, Index: index)).ToList();
        if (string.IsNullOrWhiteSpace(query))
        {
            return [.. indexed
                .OrderBy(x => rank.TryGetValue(key(x.Item), out var r) ? r : int.MaxValue)
                .ThenBy(x => x.Index)
                .Select(x => x.Item)];
        }

        return [.. indexed
            .Select(x =>
            {
                var score = FuzzyMatcher.Score(query, title(x.Item))
                    ?? (FuzzyMatcher.Score(query, location(x.Item) + " " + title(x.Item)) is { } pathScore ? pathScore / 2 : null);
                if (score is { } s && rank.TryGetValue(key(x.Item), out var r))
                {
                    score = s + Math.Max(0, MaxRecent - r);
                }

                return (x.Item, x.Index, Score: score);
            })
            .Where(x => x.Score is not null)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Index)
            .Select(x => x.Item)];
    }

    /// <summary>Moves <paramref name="key"/> to the front of the recent list, keeping at most <see cref="MaxRecent"/>.</summary>
    public static void Remember(List<string> recent, string key)
    {
        recent.Remove(key);
        recent.Insert(0, key);
        if (recent.Count > MaxRecent)
        {
            recent.RemoveRange(MaxRecent, recent.Count - MaxRecent);
        }
    }
}
