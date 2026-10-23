using System.Globalization;

namespace Ultiviewer.Core.Notebooks;

/// <summary>
/// Case-insensitive comparison that orders digit runs by value, like Explorer: "Day 2" before "Day 10".
/// </summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static NaturalStringComparer Instance { get; } = new();

    private static readonly CompareInfo Culture = CultureInfo.CurrentCulture.CompareInfo;

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                int startX = i, startY = j;
                while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                var result = CompareNumbers(x.AsSpan(startX, i - startX), y.AsSpan(startY, j - startY));
                if (result != 0)
                {
                    return result;
                }
            }
            else
            {
                int startX = i, startY = j;
                while (i < x.Length && !char.IsAsciiDigit(x[i])) i++;
                while (j < y.Length && !char.IsAsciiDigit(y[j])) j++;
                var result = Culture.Compare(x.AsSpan(startX, i - startX), y.AsSpan(startY, j - startY), CompareOptions.IgnoreCase);
                if (result != 0)
                {
                    return result;
                }
            }
        }

        var byLength = (x.Length - i).CompareTo(y.Length - j);
        return byLength != 0 ? byLength : string.CompareOrdinal(x, y);
    }

    private static int CompareNumbers(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        a = a.TrimStart('0');
        b = b.TrimStart('0');
        var byLength = a.Length.CompareTo(b.Length);
        return byLength != 0 ? byLength : a.SequenceCompareTo(b);
    }
}
