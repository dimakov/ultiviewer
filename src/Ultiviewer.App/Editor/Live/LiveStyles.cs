using System.Windows;
using System.Windows.Media;
using Ultiviewer.App.Services;

namespace Ultiviewer.App.Editor.Live;

/// <summary>Fonts and brushes for the Live view, derived from the editor palette.</summary>
public sealed class LiveStyles
{
    private readonly Dictionary<string, Brush?> _colors = new(StringComparer.OrdinalIgnoreCase);

    private LiveStyles(EditorPalette palette, bool dark, FontFamily textFont, FontFamily codeFont)
    {
        TextFont = textFont;
        CodeFont = codeFont;
        Foreground = Frozen(palette.Foreground);
        Faint = Frozen(palette.LineNumbers);
        Accent = Frozen(palette.Bookmark);
        Link = Frozen(dark ? Color.FromRgb(0x6C, 0xB6, 0xFF) : Color.FromRgb(0x0B, 0x66, 0xC2));
        TagForeground = Frozen(dark ? Color.FromRgb(0x8C, 0xC4, 0xFF) : Color.FromRgb(0x1F, 0x5F, 0xA8));
        TagBackground = Frozen(dark ? Color.FromRgb(0x1D, 0x33, 0x4A) : Color.FromRgb(0xE4, 0xEE, 0xFA));
        CodeBackground = Frozen(dark ? Color.FromRgb(0x33, 0x35, 0x38) : Color.FromRgb(0xEE, 0xF0, 0xF3));
        BlockBackground = Frozen(dark ? Color.FromRgb(0x28, 0x2A, 0x2D) : Color.FromRgb(0xF5, 0xF7, 0xF9));
        Highlight = Frozen(dark ? Color.FromRgb(0x5C, 0x50, 0x12) : Color.FromRgb(0xFF, 0xF0, 0x8A));
        QuoteBar = Frozen(dark ? Color.FromRgb(0x4A, 0x50, 0x58) : Color.FromRgb(0xD0, 0xD7, 0xDE));
        Border = Frozen(dark ? Color.FromRgb(0x3C, 0x40, 0x46) : Color.FromRgb(0xD8, 0xDE, 0xE4));
        HeaderBackground = Frozen(dark ? Color.FromRgb(0x2C, 0x2F, 0x33) : Color.FromRgb(0xF0, 0xF3, 0xF6));
        RulePen = new Pen(Border, 1);
        RulePen.Freeze();
    }

    public FontFamily TextFont { get; }

    public FontFamily CodeFont { get; }

    public Brush Foreground { get; }

    public Brush Faint { get; }

    public Brush Accent { get; }

    public Brush Link { get; }

    public Brush TagForeground { get; }

    public Brush TagBackground { get; }

    public Brush CodeBackground { get; }

    public Brush BlockBackground { get; }

    public Brush Highlight { get; }

    public Brush QuoteBar { get; }

    public Brush Border { get; }

    public Brush HeaderBackground { get; }

    public Pen RulePen { get; }

    /// <summary>Width that replaces a quote's "&gt; " so the bar has room.</summary>
    public double QuoteIndent => 18;

    /// <summary>Font size of a heading relative to body text.</summary>
    public static double HeadingScale(int level) => level switch
    {
        1 => 1.75,
        2 => 1.45,
        3 => 1.25,
        4 => 1.1,
        _ => 1.0,
    };

    public static LiveStyles Create(EditorPalette palette, bool dark, FontFamily textFont, FontFamily codeFont) =>
        new(palette, dark, textFont, codeFont);

    /// <summary>A brush for a CSS color such as <c>#D64545</c> or <c>red</c>; null when WPF can't read it.</summary>
    public Brush? ColorBrush(string? css)
    {
        if (string.IsNullOrWhiteSpace(css))
        {
            return null;
        }

        if (!_colors.TryGetValue(css, out var brush))
        {
            try
            {
                brush = new BrushConverter().ConvertFromString(css.Trim()) as Brush;
                brush?.Freeze();
            }
            catch (Exception ex) when (ex is FormatException or NotSupportedException)
            {
                brush = null;
            }

            _colors[css] = brush;
        }

        return brush;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
