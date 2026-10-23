using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using Ultiviewer.App.Services;
using Ultiviewer.App.ViewModels;

namespace Ultiviewer.App.Editor;

/// <summary>Left margin showing bookmarks (Ctrl+F2 / F2 / Shift+F2); clicking it toggles a bookmark.</summary>
public sealed class BookmarkMargin(Func<DocumentTabViewModel?> currentTab) : AbstractMargin
{
    protected override Size MeasureOverride(Size availableSize) => new(16, 0);

    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        if (oldTextView is not null)
        {
            oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
        }

        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView is not null)
        {
            newTextView.VisualLinesChanged += OnVisualLinesChanged;
        }

        InvalidateVisual();
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext drawingContext)
    {
        // Transparent background so the whole strip is clickable.
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var view = TextView;
        if (view is not { VisualLinesValid: true } || currentTab() is not { } tab)
        {
            return;
        }

        var lines = tab.BookmarkLines().ToHashSet();
        if (lines.Count == 0)
        {
            return;
        }

        var brush = new SolidColorBrush(ThemeService.Instance.Palette.Bookmark);
        foreach (var visualLine in view.VisualLines)
        {
            if (lines.Contains(visualLine.FirstDocumentLine.LineNumber))
            {
                var y = visualLine.GetTextLineVisualYPosition(visualLine.TextLines[0], VisualYPosition.LineMiddle) - view.VerticalOffset;
                drawingContext.DrawEllipse(brush, null, new Point(8, y), 4.5, 4.5);
            }
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (TextView is not { } view || currentTab() is not { } tab)
        {
            return;
        }

        var position = e.GetPosition(view);
        if (view.GetVisualLineFromVisualTop(position.Y + view.VerticalOffset) is { } line)
        {
            tab.ToggleBookmark(line.FirstDocumentLine.LineNumber);
            InvalidateVisual();
            e.Handled = true;
        }
    }
}
