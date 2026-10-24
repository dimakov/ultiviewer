using System.Text;
using System.Text.RegularExpressions;

namespace Ultiviewer.Core.Clipping;

/// <summary>The copied part of a clipboard HTML document and the page it came from.</summary>
public sealed record ClipboardHtmlFragment(string Html, Uri? SourceUrl);

/// <summary>
/// Reads Windows' clipboard HTML format (CF_HTML): a header with <c>StartFragment</c>/<c>EndFragment</c> byte
/// offsets and an optional <c>SourceURL</c>, then a document whose copied part sits between
/// <c>&lt;!--StartFragment--&gt;</c> and <c>&lt;!--EndFragment--&gt;</c>.
/// </summary>
public static partial class ClipboardHtml
{
    public static ClipboardHtmlFragment? Parse(string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return null;
        }

        Uri? source = SourceUrl().Match(data) is { Success: true } url && Uri.TryCreate(url.Groups[1].Value.Trim(), UriKind.Absolute, out var parsed) ? parsed : null;

        // The comment markers are the most reliable: they survive however the text was decoded.
        var start = StartMarker().Match(data);
        var end = EndMarker().Match(data);
        if (start.Success && end.Success && end.Index >= start.Index + start.Length)
        {
            return new ClipboardHtmlFragment(data[(start.Index + start.Length)..end.Index], source);
        }

        // Otherwise the byte offsets (they count UTF-8 bytes of the whole clipboard text).
        if (Offset(data, "StartFragment") is { } from && Offset(data, "EndFragment") is { } to)
        {
            var bytes = Encoding.UTF8.GetBytes(data);
            if (from >= 0 && to <= bytes.Length && to > from)
            {
                return new ClipboardHtmlFragment(Encoding.UTF8.GetString(bytes, from, to - from), source);
            }
        }

        var html = data.IndexOf('<');
        return html < 0 ? null : new ClipboardHtmlFragment(data[html..], source);
    }

    private static int? Offset(string data, string name) =>
        Regex.Match(data, "^" + name + @":\s*(\d+)", RegexOptions.Multiline) is { Success: true } match && int.TryParse(match.Groups[1].Value, out var value) ? value : null;

    [GeneratedRegex(@"^SourceURL:(.+)$", RegexOptions.Multiline)]
    private static partial Regex SourceUrl();

    [GeneratedRegex(@"<!--\s*StartFragment\s*-->", RegexOptions.IgnoreCase)]
    private static partial Regex StartMarker();

    [GeneratedRegex(@"<!--\s*EndFragment\s*-->", RegexOptions.IgnoreCase)]
    private static partial Regex EndMarker();
}
