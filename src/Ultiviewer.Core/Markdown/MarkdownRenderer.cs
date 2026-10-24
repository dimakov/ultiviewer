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
