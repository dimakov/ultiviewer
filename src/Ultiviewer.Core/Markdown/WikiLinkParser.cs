using Markdig;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Parsers.Inlines;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax.Inlines;

namespace Ultiviewer.Core.Markdown;

/// <summary>
/// Parses <c>[[Page title]]</c> and <c>[[Page title|label]]</c> into links with a <c>wiki:</c> URL that the
/// preview hands back to the app, which resolves the title to a page in the notebook.
/// </summary>
public sealed class WikiLinkParser : InlineParser
{
    public const string Scheme = "wiki:";

    public WikiLinkParser()
    {
        OpeningCharacters = ['['];
    }

    public override bool Match(InlineProcessor processor, ref StringSlice slice)
    {
        if (slice.PeekChar() != '[')
        {
            return false;
        }

        var text = slice.Text;
        var contentStart = slice.Start + 2;
        var close = text.IndexOf("]]", contentStart, StringComparison.Ordinal);
        if (close < 0 || close > slice.End || close == contentStart)
        {
            return false;
        }

        var content = text.AsSpan(contentStart, close - contentStart);
        if (content.IndexOfAny('[', '\n', '\r') >= 0)
        {
            return false;
        }

        var bar = content.IndexOf('|');
        var target = (bar < 0 ? content : content[..bar]).Trim().ToString();
        var label = (bar < 0 ? content : content[(bar + 1)..]).Trim().ToString();
        if (target.Length == 0)
        {
            return false;
        }

        var start = processor.GetSourcePosition(slice.Start, out var line, out var column);
        var link = new LinkInline(Scheme + Uri.EscapeDataString(target), "")
        {
            IsClosed = true,
            Line = line,
            Column = column,
            Span = new Markdig.Syntax.SourceSpan(start, start + close + 1 - slice.Start),
        };
        link.AppendChild(new LiteralInline(label.Length > 0 ? label : target));
        link.GetAttributes().AddClass("wikilink");
        processor.Inline = link;
        slice.Start = close + 2;
        return true;
    }
}

public sealed class WikiLinkExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        if (!pipeline.InlineParsers.Contains<WikiLinkParser>())
        {
            pipeline.InlineParsers.InsertBefore<LinkInlineParser>(new WikiLinkParser());
        }
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
    }
}
