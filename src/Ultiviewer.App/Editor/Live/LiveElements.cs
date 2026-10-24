using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using ICSharpCode.AvalonEdit.Rendering;

namespace Ultiviewer.App.Editor.Live;

/// <summary>
/// Source text that takes no room (hidden syntax such as <c>**</c>), or a fixed indent (a quote's <c>&gt; </c>).
/// One caret stop covers the whole range; Live mode reveals the source before the caret can land inside it.
/// </summary>
internal sealed class HiddenElement(int documentLength, double width = 0) : VisualLineElement(1, documentLength)
{
    public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) =>
        new DrawnRun(TextRunProperties, width, null);
}

/// <summary>A list bullet drawn as a dot instead of <c>-</c>, <c>*</c> or <c>+</c>.</summary>
internal sealed class BulletElement(int documentLength, Brush brush) : VisualLineElement(1, documentLength)
{
    public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
    {
        var em = TextRunProperties.FontRenderingEmSize;
        return new DrawnRun(TextRunProperties, em * 0.55, (drawing, origin) =>
            drawing.DrawEllipse(brush, null, new Point(origin.X + em * 0.25, origin.Y - em * 0.3), em * 0.15, em * 0.15));
    }
}

/// <summary>An embedded run of a fixed width that draws itself (or nothing); <c>origin</c> is on the baseline.</summary>
internal sealed class DrawnRun(TextRunProperties properties, double width, Action<DrawingContext, Point>? draw) : TextEmbeddedObject
{
    public override LineBreakCondition BreakBefore => LineBreakCondition.BreakRestrained;

    public override LineBreakCondition BreakAfter => LineBreakCondition.BreakRestrained;

    public override bool HasFixedSize => true;

    public override CharacterBufferReference CharacterBufferReference => default;

    public override int Length => 1;

    public override TextRunProperties Properties => properties;

    public override TextEmbeddedObjectMetrics Format(double remainingParagraphWidth)
    {
        if (draw is null)
        {
            return new TextEmbeddedObjectMetrics(width, 0, 0);
        }

        var family = properties.Typeface.FontFamily;
        var em = properties.FontRenderingEmSize;
        return new TextEmbeddedObjectMetrics(width, em * family.LineSpacing, em * family.Baseline);
    }

    public override Rect ComputeBoundingBox(bool rightToLeft, bool sideways) =>
        new(0, 0, width, draw is null ? 0 : properties.FontRenderingEmSize * properties.Typeface.FontFamily.LineSpacing);

    public override void Draw(DrawingContext drawingContext, Point origin, bool rightToLeft, bool sideways) => draw?.Invoke(drawingContext, origin);
}

/// <summary>A to-do checkbox that toggles its task when clicked.</summary>
internal sealed class TaskBoxVisual : FrameworkElement
{
    private readonly bool _checked;
    private readonly LiveStyles _styles;
    private readonly double _size;
    private readonly Action _toggle;

    public TaskBoxVisual(bool isChecked, LiveStyles styles, double fontSize, Action toggle)
    {
        _checked = isChecked;
        _styles = styles;
        _size = Math.Round(fontSize * 1.05);
        _toggle = toggle;
        Cursor = Cursors.Hand;
        FlowDirection = FlowDirection.LeftToRight; // the check mark isn't mirrored in right-to-left notes
        Margin = new Thickness(0, 0, 2, 0);
        ToolTip = isChecked ? "Mark as not done" : "Mark as done";
        TextBlock.SetBaselineOffset(this, _size * 0.88);
    }

    protected override Size MeasureOverride(Size availableSize) => new(_size + 2, _size);

    protected override void OnRender(DrawingContext drawingContext)
    {
        var box = new Rect(0.5, 0.5, _size - 1, _size - 1);
        if (_checked)
        {
            drawingContext.DrawRoundedRectangle(_styles.Accent, null, box, 3, 3);
            var pen = new Pen(Brushes.White, Math.Max(1.5, _size / 9)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            var check = new StreamGeometry();
            using (var context = check.Open())
            {
                context.BeginFigure(new Point(_size * 0.25, _size * 0.52), false, false);
                context.LineTo(new Point(_size * 0.43, _size * 0.7), true, false);
                context.LineTo(new Point(_size * 0.76, _size * 0.32), true, false);
            }

            drawingContext.DrawGeometry(null, pen, check);
        }
        else
        {
            drawingContext.DrawRoundedRectangle(Brushes.Transparent, new Pen(_styles.Faint, 1.2), box, 3, 3);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        _toggle();
    }
}
