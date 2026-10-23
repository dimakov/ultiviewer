namespace Ultiviewer.Core.Text;

public static class TextStatistics
{
    /// <summary>Counts runs of letters/digits (apostrophes and hyphens inside a word don't split it).</summary>
    public static int CountWords(ReadOnlySpan<char> text)
    {
        var count = 0;
        var inWord = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var wordChar = char.IsLetterOrDigit(c)
                || (inWord && (c is '\'' or '’' or '-') && i + 1 < text.Length && char.IsLetterOrDigit(text[i + 1]))
                || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.NonSpacingMark;
            if (wordChar && !inWord)
            {
                count++;
            }

            inWord = wordChar;
        }

        return count;
    }
}
