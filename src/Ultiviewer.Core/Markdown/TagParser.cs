using Markdig;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax.Inlines;

namespace Ultiviewer.Core.Markdown;

/// <summary>
/// Turns <c>#tag</c> (after whitespace or an opening bracket, starting with a letter) into a link with a
/// <c>tag:</c> URL, rendered as a chip; clicking it in the preview searches for the tag.
/// </summary>
public sealed class TagParser : InlineParser
{
    public const string Scheme = "tag:";

    public TagParser()
    {
        OpeningCharacters = ['#'];
    }

    public override bool Match(InlineProcessor processor, ref StringSlice slice)
    {
        var previous = slice.PeekCharExtra(-1);
        if (!(previous == '\0' || char.IsWhiteSpace(previous) || previous is '(' or '[' or ',' or ';'))
        {
            return false;
        }

        var text = slice.Text;
        var start = slice.Start + 1;
        if (start > slice.End || !(char.IsLetter(text[start]) || text[start] == '_'))
        {
            return false;
        }

        var end = start;
        while (end <= slice.End && (char.IsLetterOrDigit(text[end]) || text[end] is '_' or '-' or '/'))
        {
            end++;
        }

        while (end > start && text[end - 1] is '-' or '/')
        {
            end--;
        }

        var tag = text[start..end];
        var position = processor.GetSourcePosition(slice.Start, out var line, out var column);
        var link = new LinkInline(Scheme + Uri.EscapeDataString(tag.ToLowerInvariant()), "")
        {
            IsClosed = true,
            Line = line,
            Column = column,
            Span = new Markdig.Syntax.SourceSpan(position, position + end - slice.Start - 1),
        };
        link.AppendChild(new LiteralInline("#" + tag));
        link.GetAttributes().AddClass("tag");
        processor.Inline = link;
        slice.Start = end;
        return true;
    }
}

public sealed class TagExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        if (!pipeline.InlineParsers.Contains<TagParser>())
        {
            pipeline.InlineParsers.Add(new TagParser());
        }
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
    }
}
