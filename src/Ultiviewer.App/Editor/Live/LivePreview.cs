using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Ultiviewer.Core.Markdown;

namespace Ultiviewer.App.Editor.Live;

/// <summary>
/// Obsidian-style live preview inside AvalonEdit: the Markdown source stays the document, but its syntax is hidden
/// or drawn as widgets and the text is styled (by default the marks stay hidden at the caret too, see
/// LivePreview.HiddenMarks.cs; optionally they show around the caret, as in Obsidian). <see cref="LiveMarkup"/> (Core) decides what
/// each range is; the generator, transformer and background renderer draw it; this class tracks the caret's
/// reveal region, mouse clicks on links and checkboxes, and list-friendly Backspace.
/// </summary>
public sealed partial class LivePreview
{
    /// <summary>Above this size the document is shown unformatted (even chunked analysis would lag).</summary>
    private const int MaxLength = 2_000_000;

    private readonly TextEditor _editor;
    private readonly LiveElementGenerator _generator;
    private readonly LiveTransformer _transformer;
    private readonly LiveBackgroundRenderer _background;
    private readonly DispatcherTimer _structureTimer;
    private TextDocument? _document;
    private LiveMarkup? _markup;
    private bool _stale = true;
    private (int Start, int End)? _revealed;

    // Set when a document is shown: its lines were drawn for a caret position that isn't tracked yet, so the
    // first caret move redraws them all (otherwise a heading revealed at the opening caret stays revealed).
    private bool _redrawAllOnMove = true;

    // Lines under a rendered table, collapsed like a fold (AvalonEdit requires that for multi-line elements),
    // keyed by the table's first line.
    private readonly Dictionary<DocumentLine, CollapsedLineSection> _collapsed = [];

    public LivePreview(TextEditor editor, LiveStyles styles)
    {
        _editor = editor;
        Styles = styles;
        _generator = new LiveElementGenerator(this);
        _transformer = new LiveTransformer(this);
        _background = new LiveBackgroundRenderer(this);

        // Edits redraw their own lines at once; a block-level change (opening a code fence) restyles the
        // lines after it, so the whole view is redrawn shortly after typing pauses.
        _structureTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) =>
        {
            _structureTimer!.Stop();
            if (IsEnabled)
            {
                TextView.Redraw();
            }
        }, editor.Dispatcher);
        _structureTimer.Stop();

        editor.DocumentChanged += (_, _) => Track(editor.Document);
        Track(editor.Document);
    }

    public bool IsEnabled { get; private set; }

    public LiveStyles Styles { get; private set; }

    /// <summary>Folder that relative image paths resolve against (the page's folder).</summary>
    public Func<string?> BaseDirectory { get; set; } = () => null;

    /// <summary>A rendered link was clicked (or Ctrl+clicked while showing source): URL, <c>wiki:</c> or <c>tag:</c> href.</summary>
    public event Action<string>? LinkClicked;

    /// <summary>A checkbox was clicked: the 1-based line of the task.</summary>
    public event Action<int>? TaskToggled;

    internal TextView TextView => _editor.TextArea.TextView;

    /// <summary>The current document's analysis, rebuilt lazily after edits; null when Live is off or the document is too big.</summary>
    internal LiveMarkup? Markup
    {
        get
        {
            if (_stale && _document is not null)
            {
                // Unchanged chunks are reused from the previous analysis, so a keystroke re-parses one chunk.
                _markup = _document.TextLength <= MaxLength ? LiveMarkup.Analyze(_document.Text, _markup) : null;
                _stale = false;
            }

            return _markup;
        }
    }

    public void Enable()
    {
        if (IsEnabled)
        {
            return;
        }

        IsEnabled = true;
        TextView.ElementGenerators.Add(_generator);
        TextView.LineTransformers.Add(_transformer);
        TextView.BackgroundRenderers.Add(_background);
        var area = _editor.TextArea;
        area.Caret.PositionChanged += OnSelectionMoved;
        area.SelectionChanged += OnSelectionMoved;
        area.PreviewMouseLeftButtonDown += OnPreviewMouseDown;
        area.PreviewKeyDown += OnPreviewKeyDown;
        TextView.MouseMove += OnMouseMove;
        TextView.QueryCursor += OnQueryCursor;
        _revealed = null;
        TextView.Redraw();
    }

    public void Disable()
    {
        if (!IsEnabled)
        {
            return;
        }

        IsEnabled = false;
        _structureTimer.Stop();
        ReleaseTables(static (_, _) => true);
        TextView.ElementGenerators.Remove(_generator);
        TextView.LineTransformers.Remove(_transformer);
        TextView.BackgroundRenderers.Remove(_background);
        var area = _editor.TextArea;
        area.Caret.PositionChanged -= OnSelectionMoved;
        area.SelectionChanged -= OnSelectionMoved;
        area.PreviewMouseLeftButtonDown -= OnPreviewMouseDown;
        area.PreviewKeyDown -= OnPreviewKeyDown;
        TextView.MouseMove -= OnMouseMove;
        TextView.QueryCursor -= OnQueryCursor;
        TextView.Redraw();
    }

    public void SetStyles(LiveStyles styles)
    {
        Styles = styles;
        if (IsEnabled)
        {
            TextView.Redraw();
        }
    }

    internal bool IsRevealed(LiveSpan span)
    {
        var (start, end) = SelectionRange();
        return _hideMarks && _markup is { } markup
            ? markup.IsRevealedWithHiddenMarks(span, start, end)
            : span.IsRevealedBy(start, end);
    }

    internal VisualLineElement? CreateElement(LiveSpan span, int lineNumber)
    {
        var fontSize = _editor.FontSize;
        var width = Math.Max(120, TextView.ActualWidth - 8);
        return span.Style switch
        {
            LiveStyle.Marker or LiveStyle.Rule => new HiddenElement(span.Length),
            LiveStyle.QuoteMarker => new HiddenElement(span.Length, Styles.QuoteIndent),
            LiveStyle.Bullet => new BulletElement(span.Length, Styles.Foreground),
            LiveStyle.TaskBox => new InlineObjectElement(span.Length, new TaskBoxVisual(span.Value == 1, Styles, fontSize, () => TaskToggled?.Invoke(lineNumber))),
            LiveStyle.Image => new InlineObjectElement(span.Length,
                LiveWidgets.Image(span, BaseDirectory(), Styles, width, () => TextView.Redraw(), () => PlaceCaret(span.Start))),
            LiveStyle.Table when _document is not null && MarkdownTable.Parse(_document.Text, span.Start, span.End) is { } table =>
                TableElement(span, table, fontSize, width),
            _ => null,
        };
    }

    private InlineObjectElement TableElement(LiveSpan span, MarkdownTable table, double fontSize, double width)
    {
        var first = _document!.GetLineByOffset(span.Start);
        var last = _document.GetLineByOffset(span.End);
        if (last != first && !(_collapsed.TryGetValue(first, out var existing) && existing.IsCollapsed && existing.End == last))
        {
            if (existing is { IsCollapsed: true })
            {
                existing.Uncollapse();
            }

            _collapsed[first] = TextView.CollapseLines(first.NextLine, last);
        }

        return new InlineObjectElement(span.Length, LiveWidgets.Table(table, Styles, fontSize, width, PlaceCaret));
    }

    /// <summary>Uncollapses rendered tables for which <paramref name="release"/>(first line, last line) is true and redraws them.</summary>
    private void ReleaseTables(Func<DocumentLine, DocumentLine, bool> release)
    {
        foreach (var (first, section) in _collapsed.ToList())
        {
            var last = section.End;
            if (section.IsCollapsed && !first.IsDeleted && last is { IsDeleted: false } && !release(first, last))
            {
                continue;
            }

            if (section.IsCollapsed)
            {
                section.Uncollapse();
            }

            _collapsed.Remove(first);
            if (!first.IsDeleted)
            {
                Redraw((first.Offset, last is { IsDeleted: false } ? last.EndOffset : first.EndOffset));
            }
        }
    }

    private (int Start, int End) SelectionRange()
    {
        var area = _editor.TextArea;
        if (area.Selection.IsEmpty)
        {
            return (area.Caret.Offset, area.Caret.Offset);
        }

        var segment = area.Selection.SurroundingSegment;
        return (segment.Offset, segment.EndOffset);
    }

    private void Track(TextDocument? document)
    {
        if (_document is not null)
        {
            _document.Changed -= OnDocumentChanged;
        }

        _document = document;
        if (document is not null)
        {
            document.Changed += OnDocumentChanged;
        }

        _collapsed.Clear(); // a new document gets a new height tree
        _redrawAllOnMove = true;

        _stale = true;
        _revealed = null;
    }

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        _stale = true;
        _revealed = null;
        var changeEnd = e.Offset + e.InsertionLength;
        ReleaseTables((first, last) => e.Offset <= last.EndOffset && changeEnd >= first.Offset);
        if (IsEnabled)
        {
            _structureTimer.Stop();
            _structureTimer.Start();
        }
    }

    /// <summary>Redraws the lines that change between source and rendered as the caret or selection moves.</summary>
    private void OnSelectionMoved(object? sender, EventArgs e)
    {
        if (Markup is not { } markup || _document is null || SnapCaret())
        {
            return; // a snapped caret raises this again from its new place
        }

        var (start, end) = SelectionRange();
        ReleaseTables((first, last) => start <= last.EndOffset && end >= first.Offset);
        var region = markup.RevealRegion(start, end);
        if (_revealed == region)
        {
            return;
        }

        var previous = _revealed;
        _revealed = region;
        if (_redrawAllOnMove)
        {
            _redrawAllOnMove = false;
            TextView.Redraw();
            return;
        }

        if (previous is { } old)
        {
            Redraw(old);
        }

        Redraw(region);
    }

    private void Redraw((int Start, int End) range)
    {
        var length = _document?.TextLength ?? 0;
        var start = Math.Clamp(range.Start, 0, length);
        TextView.Redraw(start, Math.Clamp(range.End, start, length) - start, DispatcherPriority.Normal);
    }

    private void PlaceCaret(int offset)
    {
        if (_document is null)
        {
            return;
        }

        _editor.TextArea.ClearSelection();
        _editor.TextArea.Caret.Offset = Math.Clamp(offset, 0, _document.TextLength);
        _editor.TextArea.Focus();
    }

    /// <summary>The rendered link or tag under the mouse, if any.</summary>
    private LiveSpan? LinkAt(MouseEventArgs e)
    {
        if (Markup is not { } markup || e.OriginalSource is not ICSharpCode.AvalonEdit.Rendering.TextView)
        {
            return null;
        }

        var point = e.GetPosition(TextView) + TextView.ScrollOffset;
        if (TextView.GetPositionFloor(point) is not { } position || _document is null)
        {
            return null;
        }

        var offset = _document.GetOffset(position.Location);
        foreach (var span in markup.SpansOnLine(position.Line))
        {
            if ((span.Style is LiveStyle.Link or LiveStyle.Tag) && span.Start <= offset && offset < span.End && span.Text is not null
                && (!IsRevealed(span) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)))
            {
                return span;
            }
        }

        return null;
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1 && LinkAt(e) is { Text: { } href })
        {
            e.Handled = true;
            LinkClicked?.Invoke(href);
        }
    }

    private bool _overLink;

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var over = LinkAt(e) is not null;
        if (over != _overLink)
        {
            _overLink = over;
            Mouse.UpdateCursor();
        }
    }

    private void OnQueryCursor(object sender, QueryCursorEventArgs e)
    {
        if (_overLink)
        {
            e.Cursor = Cursors.Hand;
            e.Handled = true;
        }
    }

    /// <summary>Backspace at the start of a rendered list item removes its bullet or checkbox, as in OneNote and Obsidian.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleHiddenMarksKey(e))
        {
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Back || Keyboard.Modifiers != ModifierKeys.None || !_editor.TextArea.Selection.IsEmpty || _document is null)
        {
            return;
        }

        var caret = _editor.CaretOffset;
        var line = _document.GetLineByOffset(caret);
        var match = ListPrefix().Match(_document.GetText(line));
        if (match.Success && caret == line.Offset + match.Length)
        {
            var marker = match.Groups["marker"];
            _document.Remove(line.Offset + marker.Index, match.Length - marker.Index);
            e.Handled = true;
        }
    }

    [GeneratedRegex(@"^[ \t]*(?<marker>(?:[-*+]|\d{1,9}[.)])[ \t]+(?:\[[ xX]\][ \t]+)?)")]
    private static partial Regex ListPrefix();
}
