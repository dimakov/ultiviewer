using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Ultiviewer.Core.Markdown;

namespace Ultiviewer.App.Editor.Live;

/// <summary>Fonts, sizes, colors and decorations for the Live view; revealed syntax is drawn faint.</summary>
internal sealed class LiveTransformer(LivePreview owner) : DocumentColorizingTransformer
{
    protected override void ColorizeLine(DocumentLine line)
    {
        // A widget (table) that spans several lines owns them; there is no text to style.
        if (owner.Markup is not { } markup || CurrentContext.VisualLine.FirstDocumentLine != CurrentContext.VisualLine.LastDocumentLine)
        {
            return;
        }

        var styles = owner.Styles;
        foreach (var span in markup.SpansOnLine(line.LineNumber))
        {
            var start = Math.Max(span.Start, line.Offset);
            var end = Math.Min(span.End, line.EndOffset);
            if (end <= start)
            {
                continue;
            }

            if (span.IsReplacement)
            {
                if (span.Style != LiveStyle.Table && owner.IsRevealed(span))
                {
                    ChangeLinePart(start, end, e => e.TextRunProperties.SetForegroundBrush(styles.Faint));
                }

                continue;
            }

            switch (span.Style)
            {
                case LiveStyle.Heading:
                    var scale = LiveStyles.HeadingScale(span.Value);
                    ChangeLinePart(start, end, e =>
                    {
                        Resize(e, scale);
                        Typeface(e, weight: FontWeights.SemiBold);
                        if (span.Value >= 6)
                        {
                            e.TextRunProperties.SetForegroundBrush(styles.Faint);
                        }
                    });
                    break;
                case LiveStyle.Strong:
                    ChangeLinePart(start, end, e => Typeface(e, weight: FontWeights.Bold));
                    break;
                case LiveStyle.Emphasis:
                    ChangeLinePart(start, end, e => Typeface(e, style: FontStyles.Italic));
                    break;
                case LiveStyle.Strikethrough:
                    ChangeLinePart(start, end, e => Decorate(e, TextDecorations.Strikethrough));
                    break;
                case LiveStyle.Underline:
                    ChangeLinePart(start, end, e => Decorate(e, TextDecorations.Underline));
                    break;
                case LiveStyle.Highlight:
                    ChangeLinePart(start, end, e => e.TextRunProperties.SetBackgroundBrush(styles.Highlight));
                    break;
                case LiveStyle.Subscript or LiveStyle.Superscript:
                    // Aligning the smaller text with the top or bottom of the line shifts it without making the line taller.
                    var alignment = span.Style == LiveStyle.Subscript ? BaselineAlignment.TextBottom : BaselineAlignment.TextTop;
                    ChangeLinePart(start, end, e =>
                    {
                        e.TextRunProperties.SetBaselineAlignment(alignment);
                        Resize(e, 0.72);
                    });
                    break;
                case LiveStyle.Code:
                    ChangeLinePart(start, end, e =>
                    {
                        Typeface(e, family: styles.CodeFont);
                        Resize(e, 0.92);
                        e.TextRunProperties.SetBackgroundBrush(styles.CodeBackground);
                    });
                    break;
                case LiveStyle.Math:
                    ChangeLinePart(start, end, e =>
                    {
                        Typeface(e, family: MathFont, style: FontStyles.Italic);
                        e.TextRunProperties.SetForegroundBrush(styles.Accent);
                    });
                    break;
                case LiveStyle.Link:
                    ChangeLinePart(start, end, e =>
                    {
                        e.TextRunProperties.SetForegroundBrush(styles.Link);
                        if (!owner.IsRevealed(span))
                        {
                            Decorate(e, TextDecorations.Underline);
                        }
                    });
                    break;
                case LiveStyle.Tag:
                    ChangeLinePart(start, end, e =>
                    {
                        e.TextRunProperties.SetForegroundBrush(styles.TagForeground);
                        e.TextRunProperties.SetBackgroundBrush(styles.TagBackground);
                    });
                    break;
                case LiveStyle.Color when styles.ColorBrush(span.Text) is { } color:
                    ChangeLinePart(start, end, e => e.TextRunProperties.SetForegroundBrush(color));
                    break;
                case LiveStyle.Comment:
                    ChangeLinePart(start, end, e =>
                    {
                        e.TextRunProperties.SetForegroundBrush(styles.Faint);
                        Typeface(e, style: FontStyles.Italic);
                    });
                    break;
                case LiveStyle.CodeBlock or LiveStyle.TableRow:
                    ChangeLinePart(start, end, e =>
                    {
                        Typeface(e, family: styles.CodeFont);
                        Resize(e, 0.92);
                    });
                    break;
                case LiveStyle.CodeInfo:
                    ChangeLinePart(start, end, e =>
                    {
                        e.TextRunProperties.SetForegroundBrush(styles.Faint);
                        Resize(e, 0.85);
                    });
                    break;
                case LiveStyle.FrontMatter:
                    ChangeLinePart(start, end, e =>
                    {
                        Typeface(e, family: styles.CodeFont);
                        e.TextRunProperties.SetForegroundBrush(styles.Faint);
                    });
                    break;
                case LiveStyle.TaskDone:
                    ChangeLinePart(start, end, e =>
                    {
                        Decorate(e, TextDecorations.Strikethrough);
                        e.TextRunProperties.SetForegroundBrush(styles.Faint);
                    });
                    break;
            }
        }
    }

    private static readonly FontFamily MathFont = new("Cambria Math, Cambria, Georgia");

    private static void Resize(VisualLineElement element, double factor)
    {
        var size = element.TextRunProperties.FontRenderingEmSize * factor;
        element.TextRunProperties.SetFontRenderingEmSize(size);
        element.TextRunProperties.SetFontHintingEmSize(size);
    }

    private static void Typeface(VisualLineElement element, FontFamily? family = null, FontStyle? style = null, FontWeight? weight = null)
    {
        var current = element.TextRunProperties.Typeface;
        element.TextRunProperties.SetTypeface(new Typeface(family ?? current.FontFamily, style ?? current.Style, weight ?? current.Weight, current.Stretch));
    }

    private static void Decorate(VisualLineElement element, TextDecorationCollection decorations)
    {
        var combined = new TextDecorationCollection(element.TextRunProperties.TextDecorations ?? []);
        foreach (var decoration in decorations)
        {
            combined.Add(decoration);
        }

        combined.Freeze();
        element.TextRunProperties.SetTextDecorations(combined);
    }
}
