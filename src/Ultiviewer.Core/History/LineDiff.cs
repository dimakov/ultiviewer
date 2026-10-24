namespace Ultiviewer.Core.History;

public enum DiffKind
{
    Same,
    Removed,
    Added,
}

/// <summary>One line of a unified diff: unchanged, only in the old text, or only in the new text.</summary>
public readonly record struct DiffLine(DiffKind Kind, string Text, int OldLine, int NewLine);

/// <summary>Line-based diff (Myers' algorithm after trimming the common prefix and suffix).</summary>
public static class LineDiff
{
    /// <summary>Above this many differing lines the middle is reported as removed-then-added instead of aligned.</summary>
    private const int MaxEdits = 2000;

    public static IReadOnlyList<DiffLine> Compute(string oldText, string newText) =>
        Compute(SplitLines(oldText), SplitLines(newText));

    public static IReadOnlyList<DiffLine> Compute(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var prefix = 0;
        while (prefix < a.Count && prefix < b.Count && a[prefix] == b[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < a.Count - prefix && suffix < b.Count - prefix && a[a.Count - 1 - suffix] == b[b.Count - 1 - suffix])
        {
            suffix++;
        }

        var result = new List<DiffLine>(Math.Max(a.Count, b.Count) + 8);
        for (var i = 0; i < prefix; i++)
        {
            result.Add(new DiffLine(DiffKind.Same, a[i], i + 1, i + 1));
        }

        Middle(a, b, prefix, a.Count - suffix, prefix, b.Count - suffix, result);
        for (var i = suffix; i > 0; i--)
        {
            result.Add(new DiffLine(DiffKind.Same, a[a.Count - i], a.Count - i + 1, b.Count - i + 1));
        }

        return result;
    }

    public static IReadOnlyList<string> SplitLines(string text) =>
        text.Length == 0 ? [] : text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    /// <summary>Counts of added and removed lines, for summaries such as "+3 −1".</summary>
    public static (int Added, int Removed) Count(IReadOnlyList<DiffLine> diff) =>
        (diff.Count(d => d.Kind == DiffKind.Added), diff.Count(d => d.Kind == DiffKind.Removed));

    private static void Middle(IReadOnlyList<string> a, IReadOnlyList<string> b, int aStart, int aEnd, int bStart, int bEnd, List<DiffLine> result)
    {
        var n = aEnd - aStart;
        var m = bEnd - bStart;
        var max = n + m;
        if (max == 0)
        {
            return;
        }

        // Myers: V[k] = furthest x on diagonal k. Round d only reads diagonals -d..d of the previous round,
        // so each saved round keeps just that slice (memory grows with the square of the edit count, not n·m).
        var offset = max + 1;
        var v = new int[2 * max + 3];
        var trace = new List<int[]>();
        var found = false;
        for (var d = 0; d <= Math.Min(max, MaxEdits) && !found; d++)
        {
            trace.Add(v[(offset - d)..(offset + d + 1)]);
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]) ? v[offset + k + 1] : v[offset + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[aStart + x] == b[bStart + y])
                {
                    x++;
                    y++;
                }

                v[offset + k] = x;
                if (x >= n && y >= m)
                {
                    found = true;
                    break;
                }
            }
        }

        if (!found)
        {
            for (var i = aStart; i < aEnd; i++)
            {
                result.Add(new DiffLine(DiffKind.Removed, a[i], i + 1, 0));
            }

            for (var j = bStart; j < bEnd; j++)
            {
                result.Add(new DiffLine(DiffKind.Added, b[j], 0, j + 1));
            }

            return;
        }

        // Walk back through the saved rounds, collecting the edit path in reverse.
        var path = new List<DiffLine>();
        int px = n, py = m;
        for (var d = trace.Count - 1; d > 0; d--)
        {
            var previous = trace[d]; // diagonal k is at index k + d
            var k = px - py;
            var prevK = k == -d || (k != d && previous[k - 1 + d] < previous[k + 1 + d]) ? k + 1 : k - 1;
            var prevX = previous[prevK + d];
            var prevY = prevX - prevK;
            while (px > prevX && py > prevY)
            {
                px--;
                py--;
                path.Add(new DiffLine(DiffKind.Same, a[aStart + px], aStart + px + 1, bStart + py + 1));
            }

            if (px == prevX)
            {
                py--;
                path.Add(new DiffLine(DiffKind.Added, b[bStart + py], 0, bStart + py + 1));
            }
            else
            {
                px--;
                path.Add(new DiffLine(DiffKind.Removed, a[aStart + px], aStart + px + 1, 0));
            }
        }

        while (px > 0 && py > 0)
        {
            px--;
            py--;
            path.Add(new DiffLine(DiffKind.Same, a[aStart + px], aStart + px + 1, bStart + py + 1));
        }

        path.Reverse();
        result.AddRange(path);
    }
}
