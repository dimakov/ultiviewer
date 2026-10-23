using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Ultiviewer.App.ViewModels;

namespace Ultiviewer.App.Views;

/// <summary>A TextBlock that renders <see cref="TextSegment"/>s, highlighting the matching ones.</summary>
public sealed class HighlightedText : TextBlock
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IReadOnlyList<TextSegment>), typeof(HighlightedText),
        new PropertyMetadata(null, (d, _) => ((HighlightedText)d).Rebuild()));

    private static readonly Brush MatchBackground = Freeze(new SolidColorBrush(Color.FromArgb(0x70, 0xF2, 0xB6, 0x00)));

    public IReadOnlyList<TextSegment>? Segments
    {
        get => (IReadOnlyList<TextSegment>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    private void Rebuild()
    {
        Inlines.Clear();
        foreach (var segment in Segments ?? [])
        {
            var run = new Run(segment.Text);
            if (segment.IsMatch)
            {
                run.Background = MatchBackground;
                run.FontWeight = FontWeights.SemiBold;
            }

            Inlines.Add(run);
        }
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
