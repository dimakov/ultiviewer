using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;
using Ultiviewer.Core.Text;
using AvalonSegment = ICSharpCode.AvalonEdit.Document.TextSegment;

namespace Ultiviewer.App.Editor;

/// <summary>
/// Draws find-bar matches (strong) and occurrences of the selected word (subtle, Notepad++ "smart
/// highlighting") behind the text. Ranges are sorted; only the visible part is drawn.
/// </summary>
public sealed class SearchHighlighter : IBackgroundRenderer
{
    private readonly Brush _matchBrush = Frozen(Color.FromArgb(0x80, 0xF2, 0xB6, 0x00));
    private readonly Brush _smartBrush = Frozen(Color.FromArgb(0x45, 0x3A, 0x9B, 0xF0));
    private IReadOnlyList<TextRange> _matches = [];
    private IReadOnlyList<TextRange> _smart = [];

    public KnownLayer Layer => KnownLayer.Selection;

    public TextView? View { get; set; }

    public void SetMatches(IReadOnlyList<TextRange> matches)
    {
        _matches = matches;
        View?.InvalidateLayer(Layer);
    }

    public void SetSmart(IReadOnlyList<TextRange> ranges)
    {
        _smart = ranges;
        View?.InvalidateLayer(Layer);
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!textView.VisualLinesValid || textView.VisualLines.Count == 0 || (_matches.Count == 0 && _smart.Count == 0))
        {
            return;
        }

        var first = textView.VisualLines[0].FirstDocumentLine.Offset;
        var last = textView.VisualLines[^1].LastDocumentLine.EndOffset;
        DrawRanges(textView, drawingContext, _smart, _smartBrush, first, last);
        DrawRanges(textView, drawingContext, _matches, _matchBrush, first, last);
    }

    private static void DrawRanges(TextView view, DrawingContext context, IReadOnlyList<TextRange> ranges, Brush brush, int first, int last)
    {
        var documentLength = view.Document?.TextLength ?? 0;
        for (var i = FirstAtOrAfter(ranges, first); i < ranges.Count && ranges[i].Start <= last; i++)
        {
            var range = ranges[i];
            if (range.End > documentLength)
            {
                break;
            }

            var builder = new BackgroundGeometryBuilder { CornerRadius = 2, AlignToWholePixels = true };
            builder.AddSegment(view, new AvalonSegment { StartOffset = range.Start, Length = range.Length });
            if (builder.CreateGeometry() is { } geometry)
            {
                context.DrawGeometry(brush, null, geometry);
            }
        }
    }

    private static int FirstAtOrAfter(IReadOnlyList<TextRange> ranges, int offset)
    {
        int low = 0, high = ranges.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (ranges[mid].End < offset)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
