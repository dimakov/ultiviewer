using System.Text.RegularExpressions;

namespace Ultiviewer.Core.Links;

/// <summary>
/// Renames page titles inside <c>[[Title#heading|label]]</c> wiki links (code is skipped). Used by export,
/// which keeps the original title visible as the label, and by import, which drops a label that only repeats
/// the restored title.
/// </summary>
public static partial class WikiLinkRenamer
{
    /// <summary>
    /// Replaces each link target for which <paramref name="map"/> returns a new title.
    /// <paramref name="keepOldTitleAsLabel"/> adds <c>|old title</c> to links without a label, so they still read the same.
    /// </summary>
    public static (string Text, int Count) Rename(string text, Func<string, string?> map, bool keepOldTitleAsLabel)
    {
        var code = CodeRanges.Find(text);
        var count = 0;
        var result = WikiLink().Replace(text, match =>
        {
            if (CodeRanges.Contains(code, match.Index))
            {
                return match.Value;
            }

            var target = match.Groups["target"].Value.Trim();
            if (map(target) is not { } renamed || renamed == target)
            {
                return match.Value;
            }

            count++;
            var fragment = match.Groups["fragment"].Value;
            var label = match.Groups["label"].Success
                ? match.Groups["label"].Value
                : keepOldTitleAsLabel ? target + fragment : null;
            return label is null ? $"[[{renamed}{fragment}]]" : $"[[{renamed}{fragment}|{label}]]";
        });
        return (result, count);
    }

    /// <summary>Turns <c>[[Title|Title]]</c> (label equal to the target) back into <c>[[Title]]</c>.</summary>
    public static string DropRedundantLabels(string text)
    {
        var code = CodeRanges.Find(text);
        return WikiLink().Replace(text, match =>
        {
            if (CodeRanges.Contains(code, match.Index) || !match.Groups["label"].Success)
            {
                return match.Value;
            }

            var target = match.Groups["target"].Value.Trim() + match.Groups["fragment"].Value;
            return string.Equals(match.Groups["label"].Value.Trim(), target, StringComparison.Ordinal) ? $"[[{target}]]" : match.Value;
        });
    }

    [GeneratedRegex(@"\[\[(?<target>[^\[\]\|#\r\n]+)(?<fragment>#[^\[\]\|\r\n]*)?(?:\|(?<label>[^\[\]\r\n]*))?\]\]")]
    private static partial Regex WikiLink();
}
