using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;
using Ultiviewer.Core.Markdown;

namespace Ultiviewer.App.Editor.Live;

/// <summary>Code block and front matter panels, quote bars and horizontal rules behind the Live view's text.</summary>
internal sealed class LiveBackgroundRenderer(LivePreview owner) : IBackgroundRenderer
{
    /// <summary>How far panels reach left of the text; Live mode leaves at least this much margin.</summary>
    public const double Bleed = 10;

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (owner.Markup is not { } markup || !textView.VisualLinesValid)
        {
            return;
        }

        var styles = owner.Styles;
        var width = textView.ActualWidth;
        Rect? panel = null;
        foreach (var visualLine in textView.VisualLines)
        {
            var top = visualLine.VisualTop - textView.VerticalOffset;
            var row = new Rect(-Bleed, top, width + Bleed, visualLine.Height);
            var line = visualLine.FirstDocumentLine;
            var spans = markup.SpansOnLine(line.LineNumber);

            var inPanel = spans.Any(s => s.Style is LiveStyle.CodeBlock or LiveStyle.FrontMatter);
            if (inPanel)
            {
                panel = panel is { } open ? Rect.Union(open, row) : row;
            }
            else if (panel is { } done)
            {
                drawingContext.DrawRoundedRectangle(styles.BlockBackground, null, done, 6, 6);
                panel = null;
            }

            foreach (var span in spans)
            {
                if (span.Style == LiveStyle.QuoteMarker && !owner.IsRevealed(span))
                {
                    var x = visualLine.GetVisualPosition(visualLine.GetVisualColumn(span.Start - line.Offset), VisualYPosition.LineTop).X - textView.HorizontalOffset;
                    drawingContext.DrawRectangle(styles.QuoteBar, null, new Rect(x + 2, top, 3, visualLine.Height));
                }
                else if (span.Style == LiveStyle.Rule && !owner.IsRevealed(span))
                {
                    var y = Math.Round(top + visualLine.Height / 2) + 0.5;
                    drawingContext.DrawLine(styles.RulePen, new Point(0, y), new Point(width, y));
                }
            }
        }

        if (panel is { } last)
        {
            drawingContext.DrawRoundedRectangle(styles.BlockBackground, null, last, 6, 6);
        }
    }
}
