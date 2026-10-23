using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Ultiviewer.Core.Search;

/// <summary>Folded text plus, for every folded character, the index of the original character it came from.</summary>
public readonly record struct FoldedText(string Text, int[] Map);

/// <summary>
/// Case and accent folding for matching: "Café" and "CAFE" both become "cafe"; Hebrew points (niqqud) and other
/// combining marks are dropped. The map lets matches found in folded text be highlighted in the original.
/// </summary>
public static class TextFolding
{
    private static readonly ConcurrentDictionary<char, string> Cache = new();

    public static string Fold(string text) => FoldWithMap(text).Text;

    public static FoldedText FoldWithMap(string text)
    {
        var builder = new StringBuilder(text.Length);
        var map = new List<int>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c < 128)
            {
                builder.Append(c is >= 'A' and <= 'Z' ? (char)(c + 32) : c);
                map.Add(i);
                continue;
            }

            if (char.IsSurrogate(c))
            {
                builder.Append(c);
                map.Add(i);
                continue;
            }

            foreach (var folded in Cache.GetOrAdd(c, FoldChar))
            {
                builder.Append(folded);
                map.Add(i);
            }
        }

        return new FoldedText(builder.ToString(), [.. map]);
    }

    private static string FoldChar(char c)
    {
        var decomposed = c.ToString().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var part in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(part) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(part));
            }
        }

        // Ligatures and the like may expand (e.g. "ß" stays "ß"); an all-marks character folds to nothing.
        return builder.ToString();
    }
}
