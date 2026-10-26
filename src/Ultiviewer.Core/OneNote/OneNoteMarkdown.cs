using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ultiviewer.Core.Markdown;
using Ultiviewer.Core.Notebooks;

namespace Ultiviewer.Core.OneNote;

/// <summary>A page converted to Markdown, the files it links (pictures, attachments) and what the conversion lost.</summary>
public sealed record ConvertedPage(string Markdown, IReadOnlyList<PageAsset> Assets, IReadOnlyList<string> Notes);

/// <summary>A file to save in the page's attachment folder under <paramref name="Name"/>.</summary>
public sealed record PageAsset(string Name, byte[] Data);

/// <summary>
/// Converts a <see cref="OneNotePage"/> to the Markdown Ultiviewer writes itself: front matter with the page's
/// times, paragraphs, headings (h1–h6 styles), nested bulleted and numbered lists, to-dos (<c>- [ ]</c> / <c>- [x]</c>),
/// other tags as <c>#tags</c>, bold/italic/underline/strikethrough/sub/superscript/highlight/color, web links and
/// OneNote page links (as <c>[[wiki links]]</c>), tables, pictures and attachments. Handwriting and equations can't
/// be converted; they leave a visible placeholder and a note.
/// </summary>
public static partial class OneNoteMarkdown
{
    private static readonly HashSet<string> MonospaceFonts = new(StringComparer.OrdinalIgnoreCase)
    {
        "Consolas", "Courier New", "Courier", "Lucida Console", "Cascadia Code", "Cascadia Mono", "Lucida Sans Typewriter",
    };

    /// <param name="assetLink">The link from the page to an attachment with the given file name.</param>
    public static ConvertedPage Convert(OneNotePage page, Func<string, string> assetLink)
    {
        var writer = new Writer(assetLink);
        var blocks = new List<string>();
        if (page.Created is not null || page.Modified is not null)
        {
            var front = new StringBuilder("---\n");
            if (page.Created is { } created)
            {
                front.Append("created: ").Append(Time(created)).Append('\n');
            }

            if (page.Modified is { } modified)
            {
                front.Append("modified: ").Append(Time(modified)).Append('\n');
            }

            front.Append("source: OneNote\n---");
            blocks.Add(front.ToString());
        }

        if (page.Title.Trim().Length > 0)
        {
            blocks.Add("# " + MarkdownEscape.Text(OneLine(page.Title)));
        }

        string? lastPlaceholder = null;
        foreach (var block in page.Blocks)
        {
            var text = block switch
            {
                OneNoteOutline outline => writer.Elements(outline.Elements),
                OneNoteFreeContent free => writer.Content(free.Content, inTable: false),
                _ => "",
            };
            if (text.Length == 0 || text == lastPlaceholder)
            {
                continue; // a run of drawings leaves one placeholder
            }

            lastPlaceholder = text.StartsWith("*(", StringComparison.Ordinal) ? text : null;
            blocks.Add(text);
        }

        var markdown = string.Join("\n\n", blocks.Where(b => b.Length > 0));
        return new ConvertedPage(markdown.Length == 0 ? "" : markdown + "\n", writer.Assets, writer.SummarizeNotes());
    }

    private static string Time(DateTime time) => time.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private sealed class Writer(Func<string, string> assetLink)
    {
        private readonly HashSet<string> _assetNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _notes = [];

        public List<PageAsset> Assets { get; } = [];

        public void Note(string note) => _notes[note] = _notes.GetValueOrDefault(note) + 1;

        public List<string> SummarizeNotes() => [.. _notes.Select(n => n.Value > 1 ? $"{n.Key} (×{n.Value})" : n.Key)];

        // ---- Paragraphs and lists ----

        /// <summary>Sibling paragraphs: list items stay together as one list, other paragraphs are separate blocks.</summary>
        public string Elements(IReadOnlyList<OneNoteElement> elements)
        {
            var parts = new List<(string Text, bool ListItem)>();
            AddElements(elements, parts);
            var builder = new StringBuilder();
            for (var i = 0; i < parts.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(parts[i].ListItem && parts[i - 1].ListItem ? "\n" : "\n\n");
                }

                builder.Append(parts[i].Text);
            }

            return builder.ToString();
        }

        private void AddElements(IReadOnlyList<OneNoteElement> elements, List<(string Text, bool ListItem)> parts)
        {
            var code = new List<string>();
            var number = 0;

            void FlushCode()
            {
                if (code.Count > 0)
                {
                    var body = string.Join("\n", code);
                    var fence = new string('`', Math.Max(3, LongestRun(body, '`') + 1));
                    parts.Add((fence + "\n" + body + "\n" + fence, false));
                    code.Clear();
                }
            }

            foreach (var element in elements)
            {
                var texts = element.Contents.OfType<OneNoteText>().ToList();
                var task = texts.SelectMany(t => t.Tags).FirstOrDefault(t => t.Checkbox);
                if (element.List is null && task is null && element.Contents.Count == 1 && texts.Count == 1 && texts[0].Style is "code")
                {
                    // Consecutive code paragraphs are one code block.
                    code.Add(OneNoteReader.VisibleText(texts[0]).Replace('\u000B', '\n'));
                    if (element.Children.Count > 0)
                    {
                        FlushCode();
                        AddElements(element.Children, parts);
                    }

                    continue;
                }

                FlushCode();
                var body = Contents(element.Contents, heading: element.List is null && task is null);
                if (element.List is not null || task is not null)
                {
                    var bullet = element.List is { Numbered: true } list
                        ? $"{(number == 0 && list.Restart is > 0 ? number = list.Restart.Value : ++number)}. "
                        : "- ";
                    var marker = task is null ? bullet : "- " + (task.Completed ? "[x] " : "[ ] ");
                    var item = marker + Indent(body, bullet.Length);
                    if (element.Children.Count > 0 && Elements(element.Children) is { Length: > 0 } children)
                    {
                        item += "\n" + Indent(children, bullet.Length, first: true);
                    }

                    parts.Add((item, true));
                }
                else
                {
                    number = 0;
                    if (body.Length > 0)
                    {
                        parts.Add((body, false));
                    }

                    // Markdown can't indent plain paragraphs: their children follow at the same level.
                    AddElements(element.Children, parts);
                }
            }

            FlushCode();
        }

        /// <summary>A paragraph's contents (usually one rich text; sometimes pictures, files or a table).</summary>
        private string Contents(IReadOnlyList<OneNoteContent> contents, bool heading)
        {
            var pieces = new List<string>();
            foreach (var content in contents)
            {
                var text = content is OneNoteText rich ? Paragraph(rich, heading) : Content(content, inTable: false);
                if (text.Length > 0)
                {
                    pieces.Add(text);
                }
            }

            return string.Join("\n\n", pieces);
        }

        private string Paragraph(OneNoteText text, bool heading)
        {
            var inline = Inline(text);
            var tags = text.Tags.Where(t => !t.Checkbox && TagName(t.Label) is { Length: > 0 }).Select(t => "#" + TagName(t.Label)).Distinct().ToList();
            if (tags.Count > 0)
            {
                inline = (inline + " " + string.Join(" ", tags)).Trim();
            }

            if (inline.Length == 0)
            {
                return "";
            }

            if (heading && text.Style is { Length: 2 } style && style[0] == 'h' && style[1] is >= '1' and <= '6')
            {
                return new string('#', style[1] - '0') + " " + inline.Replace("\\\n", " ");
            }

            return text.Style switch
            {
                "quote" or "Quote" => string.Join("\n", inline.Split('\n').Select(line => "> " + line)),
                "cite" when !inline.StartsWith('*') => "*" + inline + "*",
                _ => inline,
            };
        }

        // ---- Pictures, files, tables ----

        public string Content(OneNoteContent content, bool inTable)
        {
            switch (content)
            {
                case OneNoteText text:
                    return Inline(text);
                case OneNoteImage image:
                {
                    if (image.Data is null)
                    {
                        Note($"A picture couldn't be imported: {image.Problem ?? "its data is missing"}");
                        return "*(picture not available)*";
                    }

                    var extension = ImageExtension(image.Data, image.Extension);
                    var stem = Path.GetFileNameWithoutExtension(image.FileName ?? "") is { Length: > 0 } fileStem ? fileStem : "image";
                    var name = AddAsset(stem, extension, image.Data);
                    if (extension is ".emf" or ".wmf" or ".xps" or ".bin")
                    {
                        Note("Pictures in formats a browser can't show (printouts: EMF, WMF, XPS) were kept as attachments");
                        return $"[{MarkdownEscape.Text(name)}]({Destination(assetLink(name))})";
                    }

                    var alt = MarkdownEscape.Text(OneLine(image.AltText ?? ""));
                    var picture = $"![{alt}]({Destination(assetLink(name))})";
                    return image.Link is { Length: > 0 } link ? $"[{picture}]({Destination(link)})" : picture;
                }

                case OneNoteFile file:
                {
                    if (file.Data is null)
                    {
                        Note($"An attachment ({file.Name}) couldn't be imported: {file.Problem ?? "its data is missing"}");
                        return $"*(attachment not available: {MarkdownEscape.Text(file.Name)})*";
                    }

                    var name = AddAsset(Path.GetFileNameWithoutExtension(file.Name), Path.GetExtension(file.Name), file.Data);
                    return $"[{MarkdownEscape.Text(name)}]({Destination(assetLink(name))})";
                }

                case OneNoteTable table:
                    return inTable ? TableAsText(table) : Table(table);
                case OneNoteUnsupported unsupported:
                    Note($"Content that can't be converted was left out: {unsupported.What}");
                    return "*(" + unsupported.What + " not imported)*";
                default:
                    return "";
            }
        }

        private string Table(OneNoteTable table)
        {
            var rows = table.Rows.Select(row => row.Select(Cell).ToList()).Where(row => row.Count > 0).ToList();
            if (rows.Count == 0)
            {
                return "";
            }

            var width = rows.Max(r => r.Count);
            foreach (var row in rows)
            {
                row.AddRange(Enumerable.Repeat("", width - row.Count));
            }

            var lines = new List<string>
            {
                "| " + string.Join(" | ", rows[0]) + " |",
                "|" + string.Concat(Enumerable.Repeat(" --- |", width)),
            };
            lines.AddRange(rows.Skip(1).Select(row => "| " + string.Join(" | ", row) + " |"));
            return string.Join("\n", lines);
        }

        private string Cell(IReadOnlyList<OneNoteElement> elements)
        {
            var pieces = new List<string>();
            void Walk(IReadOnlyList<OneNoteElement> items, int depth)
            {
                foreach (var element in items)
                {
                    var task = element.Contents.OfType<OneNoteText>().SelectMany(t => t.Tags).FirstOrDefault(t => t.Checkbox);
                    var text = string.Join(" ", element.Contents.Select(c => Content(c, inTable: true)).Where(t => t.Length > 0));
                    if (task is not null)
                    {
                        text = (task.Completed ? "☑ " : "☐ ") + text;
                    }
                    else if (element.List is not null)
                    {
                        text = new string('\u00A0', depth * 2) + "• " + text;
                    }

                    if (text.Length > 0)
                    {
                        pieces.Add(text.Replace("\\\n", "<br>").Replace("\n", " "));
                    }

                    Walk(element.Children, depth + 1);
                }
            }

            Walk(elements, 0);
            return string.Join("<br>", pieces).Replace("|", "\\|");
        }

        private string TableAsText(OneNoteTable table)
        {
            Note("Tables inside table cells were flattened to text");
            return string.Join(" / ", table.Rows.Select(row => string.Join(" ; ", row.Select(Cell))));
        }

        private string AddAsset(string stem, string extension, byte[] data)
        {
            stem = FileNameRules.MakeValid(stem, "attachment");
            var name = stem + extension;
            for (var n = 2; !_assetNames.Add(name); n++)
            {
                name = $"{stem} {n}{extension}";
            }

            Assets.Add(new PageAsset(name, data));
            return name;
        }

        // ---- Text runs ----

        /// <summary>A rich text as inline Markdown: formatting merged across runs, links resolved, soft breaks as hard breaks.</summary>
        public string Inline(OneNoteText text)
        {
            var segments = new List<(string Text, OneNoteStyle Style, string? Link)>();
            string? pendingLink = null;
            bool ink = false, math = false;
            foreach (var run in text.Runs)
            {
                var value = text.Text[run.Start..run.End];
                var style = run.Style;
                if (style.Hidden)
                {
                    if (style.Hyperlink && HyperlinkTarget().Match(value) is { Success: true } target)
                    {
                        pendingLink = target.Groups[1].Value;
                    }

                    continue;
                }

                if (value.Contains('\uFFFC'))
                {
                    ink = true;
                    value = value.Replace("\uFFFC", "");
                }

                if (style.Math)
                {
                    // Equation letters are already math italic characters; their structure (fractions,
                    // superscripts) can't be kept, so they are written as plain text.
                    math = true;
                    value = MathMarkers().Replace(value, "");
                    style = OneNoteStyle.Plain with { Math = true };
                }

                var link = style.Hyperlink ? pendingLink : null;
                if (!style.Hyperlink)
                {
                    pendingLink = null;
                }

                if (value.Length == 0)
                {
                    continue;
                }

                // Runs that look the same are merged, so "**a****b**" never appears.
                if (segments.Count > 0 && segments[^1].Style == style && segments[^1].Link == link)
                {
                    segments[^1] = (segments[^1].Text + value, style, link);
                }
                else
                {
                    segments.Add((value, style, link));
                }
            }

            if (ink)
            {
                Note("Handwriting inside text (ink) was left out");
            }

            if (math)
            {
                Note("Equations were imported as plain text");
            }

            var builder = new StringBuilder();
            for (var i = 0; i < segments.Count; i++)
            {
                if (segments[i].Link is { } url)
                {
                    // Consecutive segments of one link are its (formatted) text.
                    var inner = new StringBuilder();
                    var plain = new StringBuilder();
                    for (; i < segments.Count && segments[i].Link == url; i++)
                    {
                        inner.Append(Format(segments[i].Text, segments[i].Style with { Hyperlink = false, Underline = false, Color = null }));
                        plain.Append(segments[i].Text);
                    }

                    i--;
                    builder.Append(Link(inner.ToString().Trim(), plain.ToString().Trim(), url));
                    continue;
                }

                builder.Append(Format(segments[i].Text, segments[i].Style));
            }

            // Each line may start a block ("# ", "1. "), so each is escaped; lines end in hard breaks.
            var lines = builder.ToString().Split('\n').Select(line => MarkdownEscape.LineStart(line.Trim())).ToList();
            while (lines.Count > 0 && lines[^1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            while (lines.Count > 0 && lines[0].Length == 0)
            {
                lines.RemoveAt(0);
            }

            return string.Join("\\\n", lines);
        }

        private string Link(string inner, string plain, string url)
        {
            if (url.StartsWith("onenote:", StringComparison.OrdinalIgnoreCase))
            {
                // onenote:…Section.one#Page%20Title&section-id={…}&page-id={…}&end → [[Page Title]]
                var hash = url.IndexOf('#');
                if (hash >= 0)
                {
                    var page = url[(hash + 1)..].Split('&')[0];
                    try
                    {
                        page = Uri.UnescapeDataString(page).Trim();
                    }
                    catch (UriFormatException)
                    {
                    }

                    page = WikiTarget(page);
                    if (page.Length > 0)
                    {
                        Note("Links to OneNote pages became [[wiki links]] by page title");
                        return page.Equals(plain, StringComparison.Ordinal) ? $"[[{page}]]" : $"[[{page}|{WikiLabel(plain)}]]";
                    }
                }

                Note("Links to OneNote sections were kept as onenote: links");
            }

            return inner.Length == 0 ? $"<{url}>" : $"[{inner}]({Destination(url)})";
        }

        private static string Format(string text, OneNoteStyle style)
        {
            if (text.IndexOfAny(['\u000B', '\n', '\r']) >= 0)
            {
                // Formatting can't span a line break: each line is formatted on its own.
                return string.Join("\n", text.Replace("\r\n", "\n").Split(['\u000B', '\n', '\r']).Select(line => Format(line, style)));
            }

            if (style.Font is { } font && MonospaceFonts.Contains(font) && text.Trim().Length > 0)
            {
                var fence = new string('`', LongestRun(text, '`') + 1);
                return Hoist(text, t => fence + (t.StartsWith('`') || t.EndsWith('`') ? " " + t + " " : t) + fence);
            }

            var result = MarkdownEscape.Text(text);
            if (style.Strikethrough)
            {
                result = Hoist(result, t => "~~" + t + "~~");
            }

            if (style.Italic)
            {
                result = Hoist(result, t => "*" + t + "*");
            }

            if (style.Bold)
            {
                result = Hoist(result, t => "**" + t + "**");
            }

            if (style.Underline && !style.Hyperlink)
            {
                result = Hoist(result, t => "<u>" + t + "</u>");
            }

            if (style.Superscript)
            {
                result = Hoist(result, t => "<sup>" + t + "</sup>");
            }
            else if (style.Subscript)
            {
                result = Hoist(result, t => "<sub>" + t + "</sub>");
            }

            if (style.Highlight is not null)
            {
                result = Hoist(result, t => "==" + t + "==");
            }

            if (style.Color is { } color && color != 0)
            {
                result = Hoist(result, t => $"<span style=\"color:#{color:X6}\">" + t + "</span>");
            }

            return result;
        }

        /// <summary>Applies <paramref name="wrap"/> to the text without its surrounding whitespace (CommonMark needs that).</summary>
        private static string Hoist(string text, Func<string, string> wrap)
        {
            var start = 0;
            while (start < text.Length && char.IsWhiteSpace(text[start]))
            {
                start++;
            }

            var end = text.Length;
            while (end > start && char.IsWhiteSpace(text[end - 1]))
            {
                end--;
            }

            return start == end ? text : text[..start] + wrap(text[start..end]) + text[end..];
        }
    }

    /// <summary>A page title as a wiki link target: without the characters that end one (<c># | [ ]</c>).</summary>
    public static string WikiTarget(string title) => WikiBreakers().Replace(OneLine(title), " ").Trim();

    private static string WikiLabel(string text) => text.Replace('|', '/').Replace('[', '(').Replace(']', ')');

    /// <summary>A #tag from a note tag's label ("Remember for later" → remember-for-later).</summary>
    private static string TagName(string label) => TagCharacters().Replace(label.Trim().ToLowerInvariant().Replace(' ', '-'), "").Trim('-');

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

    private static string OneLine(string text) => string.Join(" ", text.Split((char[])['\n', '\r', '\u000B'], StringSplitOptions.RemoveEmptyEntries)).Trim();

    private static string Destination(string url)
    {
        url = url.Replace("\\", "\\\\"); // a backslash escapes the punctuation after it, even in a link (UNC paths)
        return url.IndexOfAny([' ', '(', ')', '<', '>']) >= 0 ? "<" + url.Replace("<", "%3C").Replace(">", "%3E") + ">" : url;
    }

    /// <summary>The file extension for picture bytes: the recorded one, else sniffed from the content.</summary>
    public static string ImageExtension(byte[] data, string? recorded)
    {
        if (recorded is { Length: > 1 } && recorded.StartsWith('.') && recorded.Length <= 6)
        {
            return recorded.ToLowerInvariant();
        }

        ReadOnlySpan<byte> d = data;
        return d switch
        {
            [0x89, 0x50, 0x4E, 0x47, ..] => ".png",
            [0xFF, 0xD8, 0xFF, ..] => ".jpg",
            [0x47, 0x49, 0x46, ..] => ".gif",
            [0x42, 0x4D, ..] => ".bmp",
            [0x49, 0x49, 0x2A, 0x00, ..] or [0x4D, 0x4D, 0x00, 0x2A, ..] => ".tif",
            [0xD7, 0xCD, 0xC6, 0x9A, ..] => ".wmf",
            [0x01, 0x00, 0x00, 0x00, ..] when d.Length > 44 && d[40] == 0x20 && d[41] == 0x45 && d[42] == 0x4D && d[43] == 0x46 => ".emf",
            [0x50, 0x4B, 0x03, 0x04, ..] => ".xps",
            _ when d.Length > 12 && d[..4].SequenceEqual("RIFF"u8) && d[8..12].SequenceEqual("WEBP"u8) => ".webp",
            _ => ".bin",
        };
    }

    private static int LongestRun(string text, char c)
    {
        int longest = 0, run = 0;
        foreach (var ch in text)
        {
            run = ch == c ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        return longest;
    }

    // \uFDDFHYPERLINK "https://…" (the hidden part of a OneNote link)
    [GeneratedRegex("HYPERLINK\\s+\"([^\"]*)\"")]
    private static partial Regex HyperlinkTarget();

    // OneNote's equation structure markers (noncharacters U+FDD0–U+FDEF).
    [GeneratedRegex("[\uFDD0-\uFDEF]")]
    private static partial Regex MathMarkers();

    [GeneratedRegex(@"\s*[#|\[\]]+\s*")]
    private static partial Regex WikiBreakers();

    [GeneratedRegex(@"[^\p{L}\p{N}_\-/]")]
    private static partial Regex TagCharacters();
}
