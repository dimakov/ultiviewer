using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Ultiviewer.Core.Markdown;

/// <summary>What a <see cref="LiveSpan"/> does in the Live (formatted editing) view.</summary>
public enum LiveStyle
{
    // Syntax that is hidden or drawn differently unless the caret or selection touches its reveal range.
    Marker,
    QuoteMarker,
    Bullet,
    TaskBox,
    Image,
    Table,
    Rule,

    // Character and line styles that always apply.
    Strong,
    Emphasis,
    Strikethrough,
    Underline,
    Highlight,
    Subscript,
    Superscript,
    Code,
    Math,
    Link,
    Tag,
    Color,
    Comment,
    Heading,
    CodeBlock,
    CodeInfo,
    FrontMatter,
    TaskDone,
    TableRow,

    /// <summary>Task metadata such as due dates, recurrence and reminders (Obsidian Tasks syntax).</summary>
    TaskMeta,
}

/// <summary>
/// A styled range of Markdown source. Replacement styles (<see cref="LiveStyle.Marker"/> … <see cref="LiveStyle.Rule"/>)
/// show as source only while the caret or selection touches [<see cref="RevealStart"/>, <see cref="RevealEnd"/>].
/// </summary>
/// <param name="Value">Heading level; 1 for a checked task box; for code block lines 1 = opening fence, 2 = closing fence.</param>
/// <param name="Text">Link or image URL, color, or code block language.</param>
/// <param name="Detail">Image alt text.</param>
public readonly record struct LiveSpan(
    int Start, int Length, LiveStyle Style, int RevealStart, int RevealEnd, int Value = 0, string? Text = null, string? Detail = null)
{
    public int End => Start + Length;

    public bool IsReplacement => Style <= LiveStyle.Rule;

    /// <summary>True when a caret or selection [<paramref name="selectionStart"/>, <paramref name="selectionEnd"/>] shows this span as source.</summary>
    public bool IsRevealedBy(int selectionStart, int selectionEnd) => selectionStart <= RevealEnd && selectionEnd >= RevealStart;
}

/// <summary>
/// The Live view's model of a Markdown document: Markdig's syntax tree (with precise source positions) flattened
/// into <see cref="LiveSpan"/>s, indexed by line. Pure and UI-free; the editor rebuilds it after each change.
/// </summary>
/// <remarks>
/// The text is analyzed in chunks split at blank lines that cannot be inside a block (see <see cref="Chunks"/>),
/// and chunk results are reused by text from the previous analysis, so a keystroke re-parses only its own chunk.
/// The price: a reference-style link whose definition is in another chunk shows as plain text.
/// </remarks>
public sealed partial class LiveMarkup
{
    // Only the syntax the Live view draws (the full preview pipeline is several times slower).
    private static readonly MarkdownPipeline Pipeline = Builder().Build();

    private static readonly MarkdownPipeline FirstChunkPipeline = Builder().UseYamlFrontMatter().Build();

    private static MarkdownPipelineBuilder Builder() => new MarkdownPipelineBuilder()
        .UseEmphasisExtras()
        .UsePipeTables()
        .UseTaskLists()
        .UseMathematics()
        .UseAutoLinks()
        .UseListExtras()
        .UseFootnotes()
        .Use<WikiLinkExtension>()
        .Use<TagExtension>()
        .UsePreciseSourceLocation();

    private static readonly IReadOnlyList<LiveSpan> None = [];

    private readonly string _text;
    private readonly int[] _lineStarts;
    private readonly int[] _lineEnds;
    private readonly List<LiveSpan>?[] _lines;
    private readonly Dictionary<int, int> _quoteScan = [];

    // Chunk text (prefixed with '1' for the first chunk, which may hold front matter) → spans relative to the chunk.
    private readonly Dictionary<string, List<LiveSpan>> _chunks = new(StringComparer.Ordinal);

    private LiveMarkup(string text)
    {
        _text = text;
        var starts = new List<int> { 0 };
        var ends = new List<int>();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '\r' or '\n')
            {
                ends.Add(i);
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                starts.Add(i + 1);
            }
        }

        ends.Add(text.Length);
        _lineStarts = [.. starts];
        _lineEnds = [.. ends];
        _lines = new List<LiveSpan>?[_lineStarts.Length];
    }

    public int LineCount => _lineStarts.Length;

    /// <summary>Analyzes <paramref name="text"/>, reusing unchanged chunks from <paramref name="previous"/>.</summary>
    public static LiveMarkup Analyze(string text, LiveMarkup? previous = null)
    {
        var markup = new LiveMarkup(text);
        foreach (var (start, end) in Chunks(text))
        {
            var key = (start == 0 ? "1" : "0") + text[start..end];
            if (previous is null || !previous._chunks.TryGetValue(key, out var spans))
            {
                spans = AnalyzeChunk(text[start..end], start == 0);
            }

            markup._chunks.TryAdd(key, spans);
            foreach (var span in spans)
            {
                markup.Register(markup.LineOf(span.Start + start), span with
                {
                    Start = span.Start + start,
                    RevealStart = span.RevealStart + start,
                    RevealEnd = span.RevealEnd + start,
                });
            }
        }

        foreach (var spans in markup._lines)
        {
            spans?.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.Length.CompareTo(a.Length));
        }

        return markup;
    }

    /// <summary>Every span, for tests.</summary>
    internal IEnumerable<LiveSpan> AllSpans => _lines.Where(l => l is not null).SelectMany(l => l!);

    /// <summary>The analysis of the whole text as one chunk, for tests.</summary>
    internal static List<LiveSpan> AnalyzeWhole(string text) => AnalyzeChunk(text, first: true);

    private static List<LiveSpan> AnalyzeChunk(string chunk, bool first)
    {
        var markup = new LiveMarkup(chunk);
        markup.Visit(Markdig.Markdown.Parse(chunk, first ? FirstChunkPipeline : Pipeline));
        return [.. markup._lines.Where(l => l is not null).SelectMany(l => l!)];
    }

    /// <summary>
    /// Splits text at blank lines after which a new top-level block starts: outside fenced code, math blocks,
    /// HTML comments and front matter, and only when the next line is not indented (so list items keep their
    /// indented continuations). Each chunk parses the same alone as in the whole document.
    /// </summary>
    internal static List<(int Start, int End)> Chunks(string text)
    {
        var chunks = new List<(int Start, int End)>();
        var chunkStart = 0;
        var position = 0;
        var fence = '\0';
        var fenceLength = 0;
        bool math = false, comment = false, frontMatter = false, afterBlank = false;
        while (position < text.Length)
        {
            var lineEnd = text.IndexOfAny(['\r', '\n'], position);
            lineEnd = lineEnd < 0 ? text.Length : lineEnd;
            var next = lineEnd == text.Length ? lineEnd : lineEnd + (text[lineEnd] == '\r' && lineEnd + 1 < text.Length && text[lineEnd + 1] == '\n' ? 2 : 1);
            var line = text.AsSpan(position, lineEnd - position);
            var trimmed = line.Trim(" \t");
            var inBlock = fence != '\0' || math || comment || frontMatter;

            if (trimmed.IsEmpty)
            {
                afterBlank |= !inBlock;
                position = next;
                continue;
            }

            if (afterBlank && !inBlock && line[0] is not (' ' or '\t') && position > chunkStart)
            {
                chunks.Add((chunkStart, position));
                chunkStart = position;
            }

            afterBlank = false;
            if (position == 0 && trimmed is "---")
            {
                frontMatter = true;
            }
            else if (frontMatter)
            {
                frontMatter = trimmed is not ("---" or "...");
            }
            else if (fence != '\0')
            {
                var run = trimmed.Length - trimmed.TrimStart(fence).Length;
                if (run >= fenceLength && trimmed.TrimStart(fence).IsEmpty)
                {
                    fence = '\0';
                }
            }
            else if (comment)
            {
                comment = !line.Contains("-->", StringComparison.Ordinal);
            }
            else if (math)
            {
                math = !trimmed.StartsWith("$$", StringComparison.Ordinal);
            }
            else if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                fence = trimmed[0];
                fenceLength = trimmed.Length - trimmed.TrimStart(fence).Length;
            }
            else if (trimmed.StartsWith("$$", StringComparison.Ordinal))
            {
                math = trimmed.Length == 2 || !trimmed[2..].Contains("$$", StringComparison.Ordinal);
            }
            else if (trimmed.StartsWith("<!--", StringComparison.Ordinal))
            {
                comment = !line.Contains("-->", StringComparison.Ordinal);
            }

            position = next;
        }

        chunks.Add((chunkStart, text.Length));
        return chunks;
    }

    /// <summary>Spans that start on a 1-based line, outer spans before the spans nested in them.</summary>
    public IReadOnlyList<LiveSpan> SpansOnLine(int lineNumber) =>
        lineNumber >= 1 && lineNumber <= _lines.Length ? _lines[lineNumber - 1] ?? None : None;

    /// <summary>
    /// The lines whose rendering depends on a caret or selection: the selected lines plus every reveal range it
    /// touches, as a source range from the first line's start to the last line's end.
    /// </summary>
    public (int Start, int End) RevealRegion(int selectionStart, int selectionEnd)
    {
        var start = selectionStart;
        var end = selectionEnd;
        for (var line = LineOf(selectionStart); line <= LineOf(selectionEnd); line++)
        {
            foreach (var span in _lines[line] ?? None)
            {
                if (span.IsReplacement || span.Style is LiveStyle.CodeBlock or LiveStyle.TableRow)
                {
                    if (span.IsRevealedBy(selectionStart, selectionEnd))
                    {
                        start = Math.Min(start, span.RevealStart);
                        end = Math.Max(end, span.RevealEnd);
                    }
                }
            }
        }

        return (_lineStarts[LineOf(start)], _lineEnds[LineOf(end)]);
    }

    /// <summary>0-based line containing <paramref name="offset"/>.</summary>
    private int LineOf(int offset)
    {
        var index = Array.BinarySearch(_lineStarts, Math.Clamp(offset, 0, _text.Length));
        return index >= 0 ? index : ~index - 1;
    }

    private (int Start, int End) LineRange(int offset)
    {
        var line = LineOf(offset);
        return (_lineStarts[line], _lineEnds[line]);
    }

    /// <summary>Adds a span; style spans that cross line breaks are split so each line holds its own pieces.</summary>
    private void Add(int start, int length, LiveStyle style, (int Start, int End) reveal, int value = 0, string? text = null, string? detail = null)
    {
        if (start < 0 || length < 0 || start + length > _text.Length)
        {
            return;
        }

        var line = LineOf(start);
        if (style is LiveStyle.Table or LiveStyle.Image || length == 0)
        {
            Register(line, new LiveSpan(start, length, style, reveal.Start, reveal.End, value, text, detail));
            return;
        }

        var end = start + length;
        while (line < _lineStarts.Length)
        {
            var pieceEnd = Math.Min(end, _lineEnds[line]);
            if (pieceEnd > start)
            {
                Register(line, new LiveSpan(start, pieceEnd - start, style, reveal.Start, reveal.End, value, text, detail));
            }

            line++;
            if (line >= _lineStarts.Length || _lineStarts[line] >= end)
            {
                break;
            }

            start = _lineStarts[line];
        }
    }

    private void Register(int line, LiveSpan span) => (_lines[line] ??= []).Add(span);

    private void Visit(MarkdownDocument document)
    {
        foreach (var block in document.Descendants<Block>())
        {
            switch (block)
            {
                case HeadingBlock heading:
                    AddHeading(heading);
                    break;
                case ThematicBreakBlock rule when rule.Parent is MarkdownDocument:
                    var ruleLine = LineRange(rule.Span.Start);
                    Add(ruleLine.Start, ruleLine.End - ruleLine.Start, LiveStyle.Rule, ruleLine);
                    break;
                case MathBlock math:
                    ForEachLine(math, (start, end) => Add(start, end - start, LiveStyle.Math, (start, end)));
                    break;
                case FencedCodeBlock fenced:
                    AddFencedCode(fenced);
                    break;
                case YamlFrontMatterBlock frontMatter:
                    ForEachLine(frontMatter, (start, end) => Add(start, end - start, LiveStyle.FrontMatter, (start, end)));
                    break;
                case CodeBlock code:
                    var codeRange = BlockRange(code);
                    ForEachLine(code, (start, end) => Add(start, end - start, LiveStyle.CodeBlock, codeRange));
                    break;
                case QuoteBlock quote:
                    AddQuoteMarkers(quote);
                    break;
                case ListItemBlock item:
                    AddListMarker(item);
                    break;
                case Table table:
                    AddTable(table);
                    break;
                case HtmlBlock html when _text.AsSpan(html.Span.Start).TrimStart().StartsWith("<!--", StringComparison.Ordinal):
                    ForEachLine(html, (start, end) => Add(start, end - start, LiveStyle.Comment, (start, end)));
                    break;
            }

            if (block is LeafBlock { Inline: { } inline } leaf)
            {
                // Inside a table every cell shows as source together, so the columns stay aligned.
                (int, int)? revealOverride = leaf.Parent is TableCell { Parent: TableRow { Parent: Table parentTable } } ? BlockRange(parentTable) : null;
                Walk(inline, revealOverride);
            }
        }
    }

    private (int Start, int End) BlockRange(Block block) =>
        (_lineStarts[LineOf(block.Span.Start)], _lineEnds[LineOf(Math.Max(block.Span.Start, block.Span.End))]);

    private void ForEachLine(Block block, Action<int, int> action)
    {
        var first = LineOf(block.Span.Start);
        var last = LineOf(Math.Max(block.Span.Start, block.Span.End));
        for (var line = first; line <= last; line++)
        {
            action(line == first ? Math.Max(_lineStarts[line], block.Span.Start) : _lineStarts[line], _lineEnds[line]);
        }
    }

    private void AddHeading(HeadingBlock heading)
    {
        var level = Math.Clamp(heading.Level, 1, 6);
        if (heading.IsSetext)
        {
            var range = BlockRange(heading);
            var underline = LineRange(Math.Max(heading.Span.Start, heading.Span.End));
            for (var line = LineOf(heading.Span.Start); line < LineOf(underline.Start); line++)
            {
                Add(_lineStarts[line], _lineEnds[line] - _lineStarts[line], LiveStyle.Heading, range, level);
            }

            Add(underline.Start, underline.End - underline.Start, LiveStyle.Marker, range);
            return;
        }

        var lineRange = LineRange(heading.Span.Start);
        var start = heading.Span.Start;
        var end = lineRange.End;
        Add(start, end - start, LiveStyle.Heading, lineRange, level);

        var position = start;
        while (position < end && _text[position] == '#')
        {
            position++;
        }

        while (position < end && _text[position] is ' ' or '\t')
        {
            position++;
        }

        Add(start, position - start, LiveStyle.Marker, lineRange);

        // Optional closing sequence: "## Title ##".
        var close = end;
        while (close > position && _text[close - 1] is ' ' or '\t')
        {
            close--;
        }

        var hashes = close;
        while (hashes > position && _text[hashes - 1] == '#')
        {
            hashes--;
        }

        if (hashes < close && hashes > position && _text[hashes - 1] is ' ' or '\t')
        {
            var markerStart = hashes;
            while (markerStart > position && _text[markerStart - 1] is ' ' or '\t')
            {
                markerStart--;
            }

            Add(markerStart, end - markerStart, LiveStyle.Marker, lineRange);
        }
    }

    private void AddFencedCode(FencedCodeBlock fenced)
    {
        var range = BlockRange(fenced);
        var first = LineOf(fenced.Span.Start);
        var last = LineOf(Math.Max(fenced.Span.Start, fenced.Span.End));
        var language = fenced.Info?.Trim() ?? "";
        for (var line = first; line <= last; line++)
        {
            var kind = line == first ? 1 : line == last && fenced.ClosingFencedCharCount > 0 ? 2 : 0;
            var start = _lineStarts[line];
            Add(start, _lineEnds[line] - start, LiveStyle.CodeBlock, range, kind, language);
        }

        // Opening fence: hide the fence characters, keep the language as a small label.
        var fenceStart = fenced.Span.Start;
        var fenceEnd = fenceStart;
        while (fenceEnd < _lineEnds[first] && _text[fenceEnd] == fenced.FencedChar)
        {
            fenceEnd++;
        }

        Add(fenceStart, fenceEnd - fenceStart, LiveStyle.Marker, range);
        if (language.Length > 0)
        {
            var infoStart = fenceEnd;
            while (infoStart < _lineEnds[first] && _text[infoStart] is ' ' or '\t')
            {
                infoStart++;
            }

            Add(infoStart, _lineEnds[first] - infoStart, LiveStyle.CodeInfo, range);
        }

        if (fenced.ClosingFencedCharCount > 0 && last > first)
        {
            var closeStart = _lineStarts[last];
            while (closeStart < _lineEnds[last] && _text[closeStart] is ' ' or '\t')
            {
                closeStart++;
            }

            Add(closeStart, _lineEnds[last] - closeStart, LiveStyle.Marker, range);
        }
    }

    private void AddQuoteMarkers(QuoteBlock quote)
    {
        var first = LineOf(quote.Span.Start);
        var last = LineOf(Math.Max(quote.Span.Start, quote.Span.End));
        for (var line = first; line <= last; line++)
        {
            var position = line == first ? quote.Span.Start : _quoteScan.GetValueOrDefault(line, _lineStarts[line]);
            var end = _lineEnds[line];
            while (position < end && _text[position] is ' ' or '\t')
            {
                position++;
            }

            if (position >= end || _text[position] != '>')
            {
                continue; // lazy continuation line
            }

            var markerEnd = position + 1;
            if (markerEnd < end && _text[markerEnd] is ' ' or '\t')
            {
                markerEnd++;
            }

            Add(position, markerEnd - position, LiveStyle.QuoteMarker, (_lineStarts[line], end));
            _quoteScan[line] = markerEnd;
        }
    }

    private void AddListMarker(ListItemBlock item)
    {
        if (item.Parent is not ListBlock list)
        {
            return;
        }

        var bullet = item.Span.Start;
        var lineEnd = _lineEnds[LineOf(bullet)];
        if (item.FirstOrDefault() is ParagraphBlock { Inline.FirstChild: TaskList task })
        {
            var box = task.Span.Start;
            var boxEnd = Math.Min(box + 3, lineEnd);
            var reveal = (bullet, boxEnd);
            Add(bullet, box - bullet, LiveStyle.Marker, reveal);
            Add(box, boxEnd - box, LiveStyle.TaskBox, reveal, task.Checked ? 1 : 0);
            var textStart = boxEnd;
            while (textStart < lineEnd && _text[textStart] is ' ' or '\t')
            {
                textStart++;
            }

            if (task.Checked && textStart < lineEnd)
            {
                Add(textStart, lineEnd - textStart, LiveStyle.TaskDone, (textStart, lineEnd));
            }

            foreach (Match field in TaskInfo.Field().Matches(_text[textStart..lineEnd]))
            {
                Add(textStart + field.Index, field.Length, LiveStyle.TaskMeta, (textStart, lineEnd));
            }

            return;
        }

        if (!list.IsOrdered && bullet < lineEnd && _text[bullet] is '-' or '*' or '+')
        {
            Add(bullet, 1, LiveStyle.Bullet, (bullet, bullet + 1));
        }
    }

    private void AddTable(Table table)
    {
        if (table.Parent is not MarkdownDocument)
        {
            return;
        }

        var range = BlockRange(table);
        Add(range.Start, range.End - range.Start, LiveStyle.Table, range);
        ForEachLine(table, (start, end) => Add(start, end - start, LiveStyle.TableRow, range));
    }

    private void Walk(ContainerInline container, (int, int)? revealOverride)
    {
        var openTags = new List<(HtmlInline Tag, string Name)>();
        for (var child = container.FirstChild; child is not null; child = child.NextSibling)
        {
            switch (child)
            {
                case EmphasisInline emphasis:
                    AddEmphasis(emphasis, revealOverride);
                    Walk(emphasis, revealOverride);
                    break;
                case LinkInline link:
                    if (AddLink(link, revealOverride))
                    {
                        Walk(link, revealOverride);
                    }

                    break;
                case CodeInline code:
                    AddDelimited(code.Span, code.DelimiterCount, LiveStyle.Code, revealOverride);
                    break;
                case MathInline math:
                    AddDelimited(math.Span, math.DelimiterCount, LiveStyle.Math, revealOverride);
                    break;
                case AutolinkInline autolink:
                    var (start, end) = (autolink.Span.Start, autolink.Span.End + 1);
                    var reveal = revealOverride ?? (start, end);
                    Add(start, 1, LiveStyle.Marker, reveal);
                    Add(start + 1, end - start - 2, LiveStyle.Link, reveal, text: autolink.IsEmail ? "mailto:" + autolink.Url : autolink.Url);
                    Add(end - 1, 1, LiveStyle.Marker, reveal);
                    break;
                case HtmlInline html:
                    AddHtml(html, openTags, revealOverride);
                    break;
                case LiteralInline { IsFirstCharacterEscaped: true } literal when literal.Span.Length == literal.Content.Length + 1:
                    // "\*": the backslash is syntax.
                    Add(literal.Span.Start, 1, LiveStyle.Marker, revealOverride ?? (literal.Span.Start, literal.Span.End + 1));
                    break;
                case ContainerInline nested:
                    Walk(nested, revealOverride);
                    break;
            }
        }
    }

    private void AddEmphasis(EmphasisInline emphasis, (int, int)? revealOverride)
    {
        LiveStyle? style = (emphasis.DelimiterChar, emphasis.DelimiterCount) switch
        {
            ('*' or '_', 1) => LiveStyle.Emphasis,
            ('*' or '_', 2) => LiveStyle.Strong,
            ('~', 2) => LiveStyle.Strikethrough,
            ('~', 1) => LiveStyle.Subscript,
            ('^', 1) => LiveStyle.Superscript,
            ('=', 2) => LiveStyle.Highlight,
            ('+', 2) => LiveStyle.Underline,
            _ => null,
        };
        if (style is { } s)
        {
            AddDelimited(emphasis.Span, emphasis.DelimiterCount, s, revealOverride);
        }
    }

    /// <summary>Markers of <paramref name="count"/> characters at both ends of <paramref name="span"/>, styled text between.</summary>
    private void AddDelimited(SourceSpan span, int count, LiveStyle style, (int, int)? revealOverride)
    {
        var start = span.Start;
        var end = span.End + 1;
        if (count <= 0 || end - start < 2 * count)
        {
            return;
        }

        var reveal = revealOverride ?? (start, end);
        Add(start, count, LiveStyle.Marker, reveal);
        Add(start + count, end - start - 2 * count, style, reveal);
        Add(end - count, count, LiveStyle.Marker, reveal);
    }

    /// <summary>Returns true when the link's label should be walked for nested formatting.</summary>
    private bool AddLink(LinkInline link, (int, int)? revealOverride)
    {
        var start = link.Span.Start;
        var end = link.Span.End + 1;
        if (end <= start || end > _text.Length)
        {
            return false;
        }

        var reveal = revealOverride ?? (start, end);
        var url = link.Url ?? "";
        if (url.StartsWith(TagParser.Scheme, StringComparison.Ordinal))
        {
            Add(start, end - start, LiveStyle.Tag, reveal, text: url);
            return false;
        }

        if (url.StartsWith(WikiLinkParser.Scheme, StringComparison.Ordinal))
        {
            var bar = _text.IndexOf('|', start + 2, end - start - 4);
            var labelStart = bar < 0 ? start + 2 : bar + 1;
            Add(start, labelStart - start, LiveStyle.Marker, reveal);
            Add(labelStart, end - 2 - labelStart, LiveStyle.Link, reveal, text: url);
            Add(end - 2, 2, LiveStyle.Marker, reveal);
            return false;
        }

        if (link.IsImage)
        {
            var alt = link.FirstChild is null ? "" : string.Concat(link.Descendants<LiteralInline>().Select(l => l.Content.ToString()));
            Add(start, end - start, LiveStyle.Image, reveal, text: url, detail: alt);
            return false;
        }

        if (link.IsAutoLink)
        {
            Add(start, end - start, LiveStyle.Link, reveal, text: url);
            return false;
        }

        if (link.FirstChild is null || link.LastChild is null)
        {
            return false; // "[](url)" has nothing to show
        }

        var label = (Start: link.FirstChild.Span.Start, End: link.LastChild.Span.End + 1);
        if (label.Start <= start || label.End >= end)
        {
            return false;
        }

        Add(start, label.Start - start, LiveStyle.Marker, reveal);
        Add(label.Start, label.End - label.Start, LiveStyle.Link, reveal, text: url);
        Add(label.End, end - label.End, LiveStyle.Marker, reveal);
        return true;
    }

    /// <summary>Pairs <c>&lt;u&gt;…&lt;/u&gt;</c>-style tags among siblings; known tags are hidden and style the text between them.</summary>
    private void AddHtml(HtmlInline html, List<(HtmlInline Tag, string Name)> openTags, (int, int)? revealOverride)
    {
        var tag = html.Tag ?? "";
        var start = html.Span.Start;
        var end = html.Span.End + 1;
        if (tag.StartsWith("<!--", StringComparison.Ordinal))
        {
            Add(start, end - start, LiveStyle.Comment, revealOverride ?? (start, end));
            return;
        }

        var match = HtmlTag().Match(tag);
        if (!match.Success)
        {
            return;
        }

        var name = match.Groups["name"].Value.ToLowerInvariant();
        if (match.Groups["close"].Success)
        {
            var index = openTags.FindLastIndex(t => t.Name == name);
            if (index < 0)
            {
                return;
            }

            var open = openTags[index].Tag;
            openTags.RemoveRange(index, openTags.Count - index);
            var openEnd = open.Span.End + 1;
            if (TagStyle(name, open.Tag) is not { } style)
            {
                return;
            }

            var reveal = revealOverride ?? (open.Span.Start, end);
            Add(open.Span.Start, openEnd - open.Span.Start, LiveStyle.Marker, reveal);
            Add(openEnd, start - openEnd, style.Style, reveal, text: style.Color);
            Add(start, end - start, LiveStyle.Marker, reveal);
        }
        else if (!match.Groups["self"].Success)
        {
            openTags.Add((html, name));
        }
    }

    private static (LiveStyle Style, string? Color)? TagStyle(string name, string openTag) => name switch
    {
        "u" or "ins" => (LiveStyle.Underline, null),
        "s" or "del" or "strike" => (LiveStyle.Strikethrough, null),
        "b" or "strong" => (LiveStyle.Strong, null),
        "i" or "em" => (LiveStyle.Emphasis, null),
        "mark" => (LiveStyle.Highlight, null),
        "sub" => (LiveStyle.Subscript, null),
        "sup" => (LiveStyle.Superscript, null),
        "code" or "kbd" => (LiveStyle.Code, null),
        "span" or "font" when CssColor().Match(openTag) is { Success: true } color => (LiveStyle.Color, color.Groups["color"].Value.Trim()),
        _ => null,
    };

    [GeneratedRegex(@"^<(?<close>/)?(?<name>[A-Za-z][A-Za-z0-9]*)\b[^>]*?(?<self>/)?>$")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"color\s*[:=]\s*[""']?(?<color>[#\w(),.%\s]+?)[""';]", RegexOptions.IgnoreCase)]
    private static partial Regex CssColor();
}
