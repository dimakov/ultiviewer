using System.Text;
using System.Text.RegularExpressions;

namespace Ultiviewer.Core.Markdown;

/// <summary>Escaping for plain text written into Markdown (pasted HTML, imported OneNote pages).</summary>
public static partial class MarkdownEscape
{
    /// <summary>Escapes characters that would otherwise turn plain text into Markdown or $math$: <c>* ` [ ] $</c>, <c>_</c> at word edges, <c>&lt;tag</c>, <c>~~</c>, <c>==</c>; a backslash only before punctuation.</summary>
    public static string Text(string text)
    {
        var builder = new StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var previous = i > 0 ? text[i - 1] : ' ';
            var next = i + 1 < text.Length ? text[i + 1] : ' ';

            // Text is often written in pieces (one per formatting run), so a character at either end may meet
            // the next piece's "~", "=" or "https://…>" and is escaped as if it did.
            var edge = i == 0 || i + 1 == text.Length;
            var escape = c switch
            {
                '\\' => i + 1 == text.Length || !(char.IsAsciiLetterOrDigit(next) || char.IsWhiteSpace(next)),
                '*' or '`' or '[' or ']' or '$' => true,
                '_' => !(char.IsLetterOrDigit(previous) && char.IsLetterOrDigit(next)),
                '<' => i + 1 == text.Length || char.IsAsciiLetter(next) || next is '/' or '!' or '?',
                '~' => edge || next == '~' || previous == '~',
                '=' => edge || next == '=' || previous == '=',
                _ => false,
            };
            if (escape)
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>Text that would start a heading, quote, list or rule at the beginning of a line is escaped.</summary>
    public static string LineStart(string line)
    {
        var match = BlockStart().Match(line);
        return match.Success ? line.Insert(match.Groups["at"].Index, "\\") : line;
    }

    [GeneratedRegex(@"^(?:(?<at>#{1,6})(?: |$)|(?<at>>)|(?<at>[-+])(?: |$)|\d+(?<at>[.)])(?: |$)|(?<at>=){3,}\s*$|(?<at>-){3,}\s*$)")]
    private static partial Regex BlockStart();
}
