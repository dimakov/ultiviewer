namespace Ultiviewer.Core.Notebooks;

public static class OrderMerger
{
    /// <summary>
    /// Orders <paramref name="items"/> by the stored <paramref name="order"/>. Stored names that no longer
    /// exist are ignored; items missing from the stored order follow, sorted by <paramref name="fallback"/>.
    /// Names are compared case-insensitively, as on NTFS.
    /// </summary>
    public static IReadOnlyList<T> Apply<T>(
        IEnumerable<T> items,
        Func<T, string> key,
        IReadOnlyList<string> order,
        IComparer<T> fallback)
    {
        var remaining = new List<T>(items);
        var result = new List<T>(remaining.Count);
        foreach (var name in order)
        {
            var index = remaining.FindIndex(item => string.Equals(key(item), name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                result.Add(remaining[index]);
                remaining.RemoveAt(index);
            }
        }

        remaining.Sort(fallback);
        result.AddRange(remaining);
        return result;
    }
}
