using System.Windows;
using System.Windows.Documents;
using System.Windows.Media.TextFormatting;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Ultiviewer.Core.Text;

namespace Ultiviewer.App.Editor;

/// <summary>
/// Gives each line the word order of its own script. AvalonEdit lays out every line in the editor's direction, so
/// a line written the other way (English code in a Hebrew note, a Hebrew line in an English one) is wrapped in a
/// directional embedding from where its prose starts (after any Markdown prefix) to its end: its words, numbers and
/// punctuation then read in its own order, aligned with the rest of the document. WPF ignores the Unicode
/// embedding characters (LRE/RLE), so the embedding is a <see cref="TextModifier"/> run, the way WPF itself gives a
/// <c>Run</c> its own direction. A strong mark of the editor's direction (LRM/RLM) just before the embedding keeps
/// the Markdown prefix (<c>- </c>, <c>&gt; </c>) of an English line on the right in a Hebrew note. (In an English
/// note WPF still shows a Hebrew line's prefix at its right end, so that line reads right to left as a whole.)
/// These runs cover no document text and are not caret stops.
/// </summary>
internal sealed class BidiLineGenerator : VisualLineElementGenerator
{
    private DocumentLine? _line;
    private int _start = -1;
    private FlowDirection _direction;

    /// <summary>The editor's direction.</summary>
    public bool RightToLeft { get; set; }

    public override void StartGeneration(ITextRunConstructionContext context)
    {
        base.StartGeneration(context);
        _line = context.VisualLine.FirstDocumentLine;
        _start = -1;
        if (_line.Length > 0)
        {
            var text = context.Document.GetText(_line);
            if (BidiText.LineDirection(text) is { } rightToLeft && rightToLeft != RightToLeft)
            {
                _start = _line.Offset + BidiText.ProseStart(text);
                _direction = rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            }
        }
    }

    public override int GetFirstInterestedOffset(int startOffset)
    {
        if (_start < 0 || _line is null || startOffset > _line.EndOffset)
        {
            return -1;
        }

        return startOffset <= _start ? _start : _line.EndOffset;
    }

    public override VisualLineElement ConstructElement(int offset) =>
        offset == _start ? new EmbeddingStart(RightToLeft ? '\u200F' : '\u200E', _direction) : new EmbeddingEnd();

    /// <summary>The editor direction's mark, then the start of an embedding in <paramref name="direction"/>.</summary>
    private sealed class EmbeddingStart(char mark, FlowDirection direction) : VisualLineElement(2, 0)
    {
        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) =>
            startVisualColumn == VisualColumn
                ? new TextCharacters(mark.ToString(), TextRunProperties)
                : new Embedding(direction, TextRunProperties);

        public override int GetNextCaretPosition(int visualColumn, LogicalDirection direction, CaretPositioningMode mode) => -1;

        public override bool IsWhitespace(int visualColumn) => true;
    }

    private sealed class EmbeddingEnd() : VisualLineElement(1, 0)
    {
        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) => new TextEndOfSegment(1);

        public override int GetNextCaretPosition(int visualColumn, LogicalDirection direction, CaretPositioningMode mode) => -1;

        public override bool IsWhitespace(int visualColumn) => true;
    }

    private sealed class Embedding(FlowDirection direction, TextRunProperties properties) : TextModifier
    {
        public override int Length => 1;

        public override TextRunProperties Properties => properties;

        public override bool HasDirectionalEmbedding => true;

        public override FlowDirection FlowDirection => direction;

        public override TextRunProperties ModifyProperties(TextRunProperties properties) => properties;
    }
}
