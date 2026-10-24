using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;
using Ultiviewer.Core.History;

namespace Ultiviewer.App.Editor;

/// <summary>Colors the lines of a unified diff shown in a read-only editor: added green, removed red.</summary>
public sealed class DiffRenderer : IBackgroundRenderer
{
    private readonly Brush _added = Frozen(Color.FromArgb(0x38, 0x2E, 0xA0, 0x43));
    private readonly Brush _removed = Frozen(Color.FromArgb(0x38, 0xD6, 0x45, 0x45));
    private readonly Brush _addedBar = Frozen(Color.FromRgb(0x2E, 0xA0, 0x43));
    private readonly Brush _removedBar = Frozen(Color.FromRgb(0xD6, 0x45, 0x45));

    public IReadOnlyList<DiffKind> Kinds { get; set; } = [];

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (Kinds.Count == 0 || !textView.VisualLinesValid)
        {
            return;
        }

        foreach (var line in textView.VisualLines)
        {
            var index = line.FirstDocumentLine.LineNumber - 1;
            if (index >= Kinds.Count || Kinds[index] == DiffKind.Same)
            {
                continue;
            }

            var added = Kinds[index] == DiffKind.Added;
            var top = line.VisualTop - textView.VerticalOffset;
            drawingContext.DrawRectangle(added ? _added : _removed, null, new Rect(0, top, textView.ActualWidth, line.Height));
            drawingContext.DrawRectangle(added ? _addedBar : _removedBar, null, new Rect(0, top, 3, line.Height));
        }
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
