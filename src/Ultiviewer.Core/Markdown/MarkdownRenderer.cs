using System.Text.RegularExpressions;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace Ultiviewer.Core.Markdown;

/// <summary>
/// Markdown → HTML for the preview. Every block element carries <c>data-line</c> (1-based source line) so the
/// preview can scroll in step with the editor and map clicks (task checkboxes, double-click) back to lines.
/// </summary>
public static partial class MarkdownRenderer
{
    public static MarkdownPipeline Pipeline { get; } = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseYamlFrontMatter()
        .Use<WikiLinkExtension>()
        .Use<TagExtension>()
        .Build();

    public static string ToHtml(string markdown)
    {
        var document = Markdig.Markdown.Parse(markdown, Pipeline);
        foreach (var block in document.Descendants<Block>())
        {
            if (block is not (MarkdownDocument or Markdig.Extensions.Yaml.YamlFrontMatterBlock))
            {
                block.GetAttributes().AddPropertyIfNotExist("data-line", (block.Line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            // Each paragraph, heading, list, quote and table takes its direction from its own text, so Hebrew
            // paragraphs read right to left next to English ones (code and math stay left to right). Text blocks
            // use dir="auto"; containers get an explicit direction from their source, because dir="auto" skips
            // children that have their own dir (a list of dir="auto" items would always be left to right).
            if (block is ParagraphBlock or HeadingBlock or Markdig.Extensions.Tables.TableCell)
            {
                block.GetAttributes().AddPropertyIfNotExist("dir", "auto");
            }
            else if (block is ListBlock or ListItemBlock or QuoteBlock or Markdig.Extensions.Tables.Table
                && block.Span.Start >= 0 && block.Span.End < markdown.Length && block.Span.Length > 0
                && Text.BidiText.Detect(markdown.Substring(block.Span.Start, block.Span.Length)) is { } rightToLeft)
            {
                block.GetAttributes().AddPropertyIfNotExist("dir", rightToLeft ? "rtl" : "ltr");
            }
        }

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();
        return ImageSize().Replace(writer.ToString(), match =>
            $"alt=\"{match.Groups["alt"].Value}\" width=\"{match.Groups["w"].Value}\"" + (match.Groups["h"].Success ? $" height=\"{match.Groups["h"].Value}\"" : ""));
    }

    /// <summary>Obsidian's image size in the alt text: <c>![diagram|300](…)</c> or <c>|300x200</c>.</summary>
    [GeneratedRegex(@"alt=""(?<alt>[^""|]*)\|(?<w>\d{1,4})(?:x(?<h>\d{1,4}))?""")]
    private static partial Regex ImageSize();
}
