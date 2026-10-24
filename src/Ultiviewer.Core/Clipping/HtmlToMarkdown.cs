using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Ultiviewer.Core.Clipping;

/// <param name="HasFormatting">
/// Whether the HTML carried anything Markdown keeps beyond plain paragraphs (headings, emphasis, links, lists,
/// tables, code, images…). Without it a paste should use the clipboard's plain text instead.
/// </param>
public sealed record MarkdownConversion(string Markdown, bool HasFormatting);

/// <summary>
/// Converts HTML (a browser or Word clipboard fragment, or a clipped article) to the Markdown the app writes
/// itself: <c>**bold**</c>, <c>*italic*</c>, <c>~~strike~~</c>, <c>&lt;u&gt;</c>, <c>&lt;sup&gt;</c>/<c>&lt;sub&gt;</c>,
/// <c>==highlight==</c>, links, images, nested and task lists, quotes, fenced code, GitHub tables and rules.
/// Inline styles count too (Google Docs and Word use <c>font-weight</c>, <c>font-style</c>, <c>text-decoration</c>),
/// and Word's list paragraphs (<c>mso-list</c>) become real lists. Scripts, forms, hidden and decorative parts
/// are dropped. Image sources go through a callback so the caller can store them as attachments.
/// </summary>
public sealed partial class HtmlToMarkdown
{
    private static readonly HashSet<string> BlockTags =
    [
        "address", "article", "aside", "blockquote", "body", "center", "dd", "details", "dialog", "div", "dl", "dt",
        "fieldset", "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hgroup",
        "hr", "html", "li", "main", "nav", "ol", "p", "pre", "section", "summary", "table", "tbody", "td", "tfoot",
        "th", "thead", "tr", "ul",
    ];

    private static readonly HashSet<string> SkippedTags =
    [
        "script", "style", "noscript", "template", "iframe", "object", "embed", "svg", "canvas", "button", "select",
        "textarea", "head", "title", "meta", "link", "video", "audio", "source", "track", "map", "area", "dialog",
    ];

    private readonly Uri? _baseUri;
    private readonly Func<string, string, string?>? _image;
    private bool _formatted;

    /// <param name="baseUri">Resolves relative links and images (the page's address).</param>
    /// <param name="image">
    /// Called with each image's absolute source and alt text; returns the link to write (for example a saved
    /// attachment), or null to leave the image out. Without it images keep their source.
    /// </param>
    public HtmlToMarkdown(Uri? baseUri = null, Func<string, string, string?>? image = null)
    {
        _baseUri = baseUri;
        _image = image;
    }

    public MarkdownConversion Convert(string html)
    {
        var document = new HtmlParser().ParseDocument(html);
        return Convert(document.Body ?? document.DocumentElement);
    }

    public MarkdownConversion Convert(IElement root)
    {
        _formatted = false;
        var markdown = string.Join("\n\n", Blocks(root.ChildNodes));
        markdown = ExtraBlankLines().Replace(markdown, "\n\n").Trim('\n', ' ');
        return new MarkdownConversion(markdown, _formatted);
    }

    // ---- Blocks ----

    private List<string> Blocks(IEnumerable<INode> nodes)
    {
        var blocks = new List<string>();
        var inline = new StringBuilder();

        void Flush()
        {
            blocks.AddRange(Paragraphs(inline.ToString()));
            inline.Clear();
        }

        var list = nodes.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is IElement element)
            {
                if (Skip(element))
                {
                    continue;
                }

                if (IsWordListItem(element))
                {
                    Flush();
                    var items = new List<IElement>();
                    for (; i < list.Count; i++)
                    {
                        if (list[i] is IElement item && IsWordListItem(item))
                        {
                            items.Add(item);
                        }
                        else if (list[i] is not IComment && !(list[i] is IText text && string.IsNullOrWhiteSpace(text.Data)))
                        {
                            break;
                        }
                    }

                    i--;
                    blocks.Add(WordList(items));
                    continue;
                }

                if (BlockTags.Contains(element.LocalName) || HasBlockInside(element))
                {
                    Flush();
                    blocks.AddRange(Block(element).Where(b => b.Length > 0));
                    continue;
                }
            }

            inline.Append(Inline(list[i]));
        }

        Flush();
        return blocks;
    }

    private IEnumerable<string> Block(IElement element)
    {
        switch (element.LocalName)
        {
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
            {
                var text = WholeBold().Replace(OneLine(InlineChildren(element)), "$1");
                if (text.Length == 0)
                {
                    return [];
                }

                _formatted = true;
                return [new string('#', element.LocalName[1] - '0') + " " + text];
            }

            case "p":
                return Paragraphs(InlineChildren(element));
            case "ul" or "ol":
                _formatted = true;
                return [List(element)];
            case "li":
                _formatted = true;
                return [Indent("- " + ItemBody(element), 2)];
            case "blockquote":
            {
                var inner = string.Join("\n\n", Blocks(element.ChildNodes));
                if (inner.Length == 0)
                {
                    return [];
                }

                _formatted = true;
                return [string.Join("\n", inner.Split('\n').Select(line => line.Length == 0 ? ">" : "> " + line))];
            }

            case "pre":
                _formatted = true;
                return [CodeBlock(element)];
            case "table":
                return Table(element);
            case "hr":
                _formatted = true;
                return ["---"];
            case "figcaption":
                return Paragraphs(InlineChildren(element)).Select(p => Wrap(p, "*", "*"));
            case "dt":
                return Paragraphs(InlineChildren(element)).Select(p => Wrap(p, "**", "**"));
            default:
                return Blocks(element.ChildNodes);
        }
    }

    /// <summary>Inline Markdown split into paragraphs: a single <c>&lt;br&gt;</c> is a hard break, two start a new paragraph.</summary>
    private static IEnumerable<string> Paragraphs(string inline)
    {
        var lines = inline.Split('\n').Select(l => l.Trim()).ToList();
        var paragraph = new List<string>();
        foreach (var line in lines.Append(""))
        {
            if (line.Length > 0)
            {
                paragraph.Add(EscapeLineStart(line));
                continue;
            }

            if (paragraph.Count > 0)
            {
                var text = string.Join("\\\n", paragraph).Replace("****", "").Replace("~~~~", "");
                paragraph.Clear();
                if (text.Trim().Length > 0)
                {
                    yield return text;
                }
            }
        }
    }

    private string List(IElement list)
    {
        var ordered = list.LocalName == "ol";
        var number = ordered && int.TryParse(list.GetAttribute("start"), out var start) ? start : 1;
        var items = new List<string>();
        foreach (var child in list.Children)
        {
            if (Skip(child))
            {
                continue;
            }

            if (child.LocalName is "ul" or "ol")
            {
                // A list right inside a list (not inside an item) belongs to the previous item.
                var nested = List(child);
                items.Add(items.Count > 0 ? items[^1] + "\n" + Indent(nested, ordered ? 3 : 2, first: true) : nested);
                if (items.Count > 1)
                {
                    items.RemoveAt(items.Count - 2);
                }

                continue;
            }

            var marker = ordered ? $"{number++}. " : "- ";
            items.Add(Indent(marker + ItemBody(child), marker.Length));
        }

        var loose = items.Any(i => i.Contains("\n\n", StringComparison.Ordinal));
        return string.Join(loose ? "\n\n" : "\n", items);
    }

    /// <summary>A list item's content: a task box, its paragraphs, and nested lists right under the text.</summary>
    private string ItemBody(IElement item)
    {
        var box = item.Children.FirstOrDefault(c => c.LocalName == "input" && c.GetAttribute("type") == "checkbox")
            ?? item.Children.FirstOrDefault(c => c.LocalName == "p")?.Children.FirstOrDefault(c => c.LocalName == "input" && c.GetAttribute("type") == "checkbox");
        var blocks = Blocks(item.ChildNodes);
        var builder = new StringBuilder(box is null ? "" : box.HasAttribute("checked") ? "[x] " : "[ ] ");
        for (var i = 0; i < blocks.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(ListStart().IsMatch(blocks[i]) ? "\n" : "\n\n");
            }

            builder.Append(blocks[i]);
        }

        return builder.ToString();
    }

    /// <summary>Word's list paragraphs (<c>MsoListParagraph</c>, <c>mso-list: l0 level2</c>) as a nested list.</summary>
    private string WordList(List<IElement> paragraphs)
    {
        _formatted = true;
        var lines = new List<string>();
        foreach (var paragraph in paragraphs)
        {
            var level = WordListLevel().Match(paragraph.GetAttribute("style") ?? "") is { Success: true } match ? int.Parse(match.Groups[1].Value) : 1;
            var marker = paragraph.QuerySelectorAll("[style]").FirstOrDefault(e => IsWordListMarker(e))?.TextContent ?? "";
            var ordered = OrderedMarker().IsMatch(Collapse(marker).Trim());
            var text = OneLine(InlineChildren(paragraph));
            lines.Add(new string(' ', 4 * (Math.Clamp(level, 1, 9) - 1)) + (ordered ? "1. " : "- ") + EscapeLineStart(text));
        }

        return string.Join("\n", lines);
    }

    private string CodeBlock(IElement pre)
    {
        var code = pre.Children.FirstOrDefault(c => c.LocalName == "code") ?? pre;
        var language = Language(code) ?? Language(pre) ?? (pre.ParentElement is { } parent ? Language(parent) : null) ?? "";
        var text = PreText(pre).Replace("\r\n", "\n").Trim('\n');
        var fence = new string('`', Math.Max(3, LongestRun(text, '`') + 1));
        return fence + language + "\n" + text + "\n" + fence;
    }

    private static string PreText(INode node)
    {
        var builder = new StringBuilder();
        foreach (var child in node.ChildNodes)
        {
            switch (child)
            {
                case IText text:
                    builder.Append(text.Data);
                    break;
                case IElement { LocalName: "br" }:
                    builder.Append('\n');
                    break;
                case IElement element:
                    builder.Append(PreText(element));
                    if (element.LocalName is "div" or "p" or "tr" or "li" && (builder.Length == 0 || builder[^1] != '\n'))
                    {
                        builder.Append('\n'); // editors (VS Code, GitHub) put each line in its own element
                    }

                    break;
            }
        }

        return builder.ToString().Replace(' ', ' ');
    }

    private static string? Language(IElement element) =>
        element.ClassList.Select(c => LanguageClass().Match(c)).FirstOrDefault(m => m.Success)?.Groups[1].Value.ToLowerInvariant();

    private IEnumerable<string> Table(IElement table)
    {
        var rows = table.QuerySelectorAll("tr").Where(r => r.Closest("table") == table).ToList();
        var cells = rows.Select(r => r.Children.Where(c => c.LocalName is "td" or "th").ToList()).Where(r => r.Count > 0).ToList();
        var layout = cells.Sum(r => r.Count) <= 1
            || cells.SelectMany(r => r).Any(c => c.QuerySelector("table, ul, ol, h1, h2, h3, pre, blockquote") is not null);
        if (cells.Count == 0 || layout)
        {
            // A table used for layout: its cells' content, in reading order.
            return cells.Count == 0 ? Blocks(table.ChildNodes) : cells.SelectMany(r => r).SelectMany(c => Blocks(c.ChildNodes)).ToList();
        }

        _formatted = true;
        var texts = cells.Select(row => row.SelectMany(c => Enumerable.Repeat(Cell(c), 1)
            .Concat(Enumerable.Repeat("", Math.Max(0, (int.TryParse(c.GetAttribute("colspan"), out var span) ? span : 1) - 1)))).ToList()).ToList();
        var width = texts.Max(r => r.Count);
        foreach (var row in texts)
        {
            row.AddRange(Enumerable.Repeat("", width - row.Count));
        }

        var alignments = Enumerable.Range(0, width).Select(i => i < cells[0].Count ? Alignment(cells[0][i]) : "---").ToList();
        var lines = new List<string>
        {
            "| " + string.Join(" | ", texts[0]) + " |",
            "| " + string.Join(" | ", alignments) + " |",
        };
        lines.AddRange(texts.Skip(1).Select(row => "| " + string.Join(" | ", row) + " |"));
        return [string.Join("\n", lines)];
    }

    private string Cell(IElement cell) =>
        string.Join("<br>", Blocks(cell.ChildNodes).Select(b => b.Replace("\\\n", "<br>").Replace('\n', ' ')))
            .Replace("|", "\\|")
            .Trim();

    private static string Alignment(IElement cell)
    {
        var align = (cell.GetAttribute("align") ?? StyleValue(cell, "text-align") ?? "").ToLowerInvariant();
        return align switch
        {
            "center" => ":---:",
            "right" => "---:",
            "left" => ":---",
            _ => "---",
        };
    }

    // ---- Inline ----

    private string Inline(INode node) => node switch
    {
        IText text => EscapeText(Collapse(text.Data)),
        IElement element => InlineElement(element),
        _ => "",
    };

    private string InlineChildren(INode node) => string.Concat(node.ChildNodes.Select(Inline));

    private string InlineElement(IElement element)
    {
        if (Skip(element))
        {
            return "";
        }

        switch (element.LocalName)
        {
            case "br":
                return "\n";
            case "img":
                return Image(element);
            case "a":
                return Link(element);
            case "input":
                return "";
            case "code" or "kbd" or "samp" or "tt":
                var code = Collapse(element.TextContent);
                if (code.Trim().Length == 0)
                {
                    return code;
                }

                _formatted = true;
                return CodeSpan(code);
        }

        var content = InlineChildren(element);
        var name = element.LocalName;
        var weight = StyleValue(element, "font-weight");
        var fontStyle = StyleValue(element, "font-style");
        var decoration = (StyleValue(element, "text-decoration") ?? "") + " " + (StyleValue(element, "text-decoration-line") ?? "");
        var verticalAlign = StyleValue(element, "vertical-align");

        var bold = name is "b" or "strong" ? weight is not ("normal" or "400" or "300" or "lighter") : weight is "bold" or "bolder" or "600" or "700" or "800" or "900";
        var italic = name is "i" or "em" or "cite" or "dfn" or "var" ? fontStyle is not "normal" : fontStyle is "italic" or "oblique";
        var strike = name is "s" or "strike" or "del" || decoration.Contains("line-through", StringComparison.Ordinal);
        var underline = name is "u" or "ins" || decoration.Contains("underline", StringComparison.Ordinal);
        var superscript = name == "sup" || verticalAlign == "super";
        var subscript = name == "sub" || verticalAlign == "sub";

        if (strike)
        {
            content = Wrap(content, "~~", "~~");
        }

        if (italic)
        {
            content = Wrap(content, "*", "*");
        }

        if (bold)
        {
            content = Wrap(content, "**", "**");
        }

        if (underline && element.Closest("a") is null)
        {
            content = Wrap(content, "<u>", "</u>");
        }

        if (superscript)
        {
            content = Wrap(content, "<sup>", "</sup>");
        }
        else if (subscript)
        {
            content = Wrap(content, "<sub>", "</sub>");
        }

        if (name == "mark")
        {
            content = Wrap(content, "==", "==");
        }

        return content;
    }

    private string Link(IElement anchor)
    {
        var text = OneLine(InlineChildren(anchor));
        var href = anchor.GetAttribute("href")?.Trim();
        if (string.IsNullOrEmpty(href) || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || text.Length == 0)
        {
            return text;
        }

        var url = Resolve(href);
        if (url.StartsWith('#'))
        {
            return text; // a jump inside the source page means nothing here
        }

        _formatted = true;
        var plain = Unescape(text);
        if ((plain == url || plain == href) && AutoLink().IsMatch(url))
        {
            return "<" + url + ">";
        }

        return "[" + text + "](" + Destination(url) + ")";
    }

    private string Image(IElement image)
    {
        if (ImageSource(image) is not { } source)
        {
            return "";
        }

        var alt = Collapse(image.GetAttribute("alt") ?? "").Trim();
        var absolute = Resolve(source);
        var target = _image is null ? absolute : _image(absolute, alt);
        if (target is null)
        {
            return "";
        }

        _formatted = true;
        return "![" + EscapeText(alt) + "](" + Destination(target) + ")";
    }

    /// <summary>The real picture of lazy-loading pages (<c>data-src</c>, the largest <c>srcset</c> entry) before a placeholder <c>src</c>.</summary>
    private static string? ImageSource(IElement image)
    {
        foreach (var attribute in new[] { "data-src", "data-original", "data-lazy-src", "data-url" })
        {
            if (image.GetAttribute(attribute) is { Length: > 0 } lazy && !lazy.StartsWith("data:", StringComparison.Ordinal))
            {
                return lazy.Trim();
            }
        }

        var src = image.GetAttribute("src")?.Trim();
        var placeholder = src is null || src.Length == 0 || src.StartsWith("data:", StringComparison.Ordinal) && src.Length < 400;
        if (placeholder && (image.GetAttribute("srcset") ?? image.GetAttribute("data-srcset")) is { Length: > 0 } srcset)
        {
            return srcset.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(candidate => candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length > 0)
                .MaxBy(parts => parts.Length > 1 && double.TryParse(parts[1].TrimEnd('w', 'x'), System.Globalization.CultureInfo.InvariantCulture, out var size) ? size : 0)?[0];
        }

        return string.IsNullOrEmpty(src) ? null : src;
    }

    private string Resolve(string url)
    {
        if (_baseUri is null || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        return Uri.TryCreate(_baseUri, url, out var absolute) ? absolute.AbsoluteUri : url;
    }

    private static string Destination(string url) =>
        url.IndexOfAny([' ', '(', ')', '<', '>']) >= 0 ? "<" + url.Replace("<", "%3C").Replace(">", "%3E") + ">" : url;

    private static string CodeSpan(string code)
    {
        var fence = new string('`', LongestRun(code, '`') + 1);
        var padding = code.StartsWith('`') || code.EndsWith('`') ? " " : "";
        return fence + padding + code + padding + fence;
    }

    // ---- Helpers ----

    private static bool Skip(IElement element)
    {
        if (SkippedTags.Contains(element.LocalName) || element.HasAttribute("hidden") || element.GetAttribute("aria-hidden") == "true")
        {
            return true;
        }

        var style = element.GetAttribute("style");
        return style is not null && (DisplayNone().IsMatch(style) || IsWordListMarker(element));
    }

    private static bool IsWordListMarker(IElement element) =>
        element.GetAttribute("style") is { } style && style.Contains("mso-list:", StringComparison.OrdinalIgnoreCase)
        && WordListIgnore().IsMatch(style);

    private static bool IsWordListItem(IElement element) =>
        element.LocalName == "p"
        && ((element.ClassName ?? "").StartsWith("MsoListParagraph", StringComparison.OrdinalIgnoreCase)
            || element.GetAttribute("style") is { } style && WordListLevel().IsMatch(style));

    private static bool HasBlockInside(IElement element) =>
        element.Descendants<IElement>().Any(e => BlockTags.Contains(e.LocalName) && e.LocalName is not ("html" or "body"));

    private static string? StyleValue(IElement element, string property)
    {
        if (element.GetAttribute("style") is not { } style)
        {
            return null;
        }

        foreach (var declaration in style.Split(';'))
        {
            var colon = declaration.IndexOf(':');
            if (colon > 0 && declaration[..colon].Trim().Equals(property, StringComparison.OrdinalIgnoreCase))
            {
                return declaration[(colon + 1)..].Replace("!important", "").Trim().ToLowerInvariant();
            }
        }

        return null;
    }

    /// <summary>Puts delimiters around the text, keeping surrounding whitespace outside (CommonMark needs that).</summary>
    private string Wrap(string content, string open, string close)
    {
        var start = 0;
        while (start < content.Length && char.IsWhiteSpace(content[start]))
        {
            start++;
        }

        var end = content.Length;
        while (end > start && char.IsWhiteSpace(content[end - 1]))
        {
            end--;
        }

        if (start == end)
        {
            return content;
        }

        _formatted = true;
        return content[..start] + open + content[start..end] + close + content[end..];
    }

    private static string Indent(string text, int width, bool first = false)
    {
        var pad = new string(' ', width);
        var lines = text.Split('\n');
        for (var i = first ? 0 : 1; i < lines.Length; i++)
        {
            if (lines[i].Length > 0)
            {
                lines[i] = pad + lines[i];
            }
        }

        return string.Join("\n", lines);
    }

    private static string OneLine(string inline) => Collapse(inline.Replace("\n", " ")).Trim();

    /// <summary>Whitespace (including no-break spaces and newlines) collapsed to single spaces, as browsers show it.</summary>
    private static string Collapse(string text) => Whitespace().Replace(text, " ");

    /// <summary>Escapes characters that would otherwise turn plain text into Markdown or math.</summary>
    private static string EscapeText(string text)
    {
        var builder = new StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var previous = i > 0 ? text[i - 1] : ' ';
            var next = i + 1 < text.Length ? text[i + 1] : ' ';
            var escape = c switch
            {
                '\\' => char.IsAsciiLetterOrDigit(next) || char.IsWhiteSpace(next) ? false : true,
                '*' or '`' or '[' or ']' or '$' => true,
                '_' => !(char.IsLetterOrDigit(previous) && char.IsLetterOrDigit(next)),
                '<' => char.IsAsciiLetter(next) || next is '/' or '!' or '?',
                '~' => next == '~' || previous == '~',
                '=' => next == '=' || previous == '=',
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

    private static string Unescape(string text) => Escaped().Replace(text, "$1");

    /// <summary>Text that would start a heading, quote, list or rule at the beginning of a line is escaped.</summary>
    private static string EscapeLineStart(string line)
    {
        var match = BlockStart().Match(line);
        return match.Success ? line.Insert(match.Groups["at"].Index, "\\") : line;
    }

    private static int LongestRun(string text, char c)
    {
        var longest = 0;
        var run = 0;
        foreach (var ch in text)
        {
            run = ch == c ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        return longest;
    }

    [GeneratedRegex(@"[\s ]+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExtraBlankLines();

    [GeneratedRegex(@"^\*\*(.+)\*\*$")]
    private static partial Regex WholeBold();

    [GeneratedRegex(@"^(?:[-+*]|\d+[.)]) ")]
    private static partial Regex ListStart();

    [GeneratedRegex(@"^(?:(?<at>#{1,6})(?: |$)|(?<at>>)|(?<at>[-+])(?: |$)|\d+(?<at>[.)])(?: |$)|(?<at>=){3,}\s*$|(?<at>-){3,}\s*$)")]
    private static partial Regex BlockStart();

    [GeneratedRegex(@"\\([\\`*_{}\[\]()#+\-.!<>~=$|])")]
    private static partial Regex Escaped();

    [GeneratedRegex(@"^(?:https?://|mailto:)[^\s<>]+$", RegexOptions.IgnoreCase)]
    private static partial Regex AutoLink();

    [GeneratedRegex(@"^(?:language|lang|highlight-source|brush:?)-?([a-z0-9+#]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex LanguageClass();

    [GeneratedRegex(@"display\s*:\s*none", RegexOptions.IgnoreCase)]
    private static partial Regex DisplayNone();

    [GeneratedRegex(@"mso-list\s*:\s*[^;]*\blevel(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex WordListLevel();

    [GeneratedRegex(@"mso-list\s*:\s*Ignore", RegexOptions.IgnoreCase)]
    private static partial Regex WordListIgnore();

    [GeneratedRegex(@"^\(?(?:\d+|[a-zA-Z]|[ivxlcIVXLC]+)[.)]$")]
    private static partial Regex OrderedMarker();
}
