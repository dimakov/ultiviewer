using ICSharpCode.AvalonEdit.Rendering;
using Ultiviewer.Core.Markdown;

namespace Ultiviewer.App.Editor.Live;

/// <summary>
/// Replaces syntax that is not under the caret: markers disappear, bullets become dots, task boxes become
/// checkboxes, images and tables become widgets. Revealed spans are left as text for the transformer.
/// </summary>
internal sealed class LiveElementGenerator(LivePreview owner) : VisualLineElementGenerator
{
    private LiveMarkup? _markup;

    public override void StartGeneration(ITextRunConstructionContext context)
    {
        base.StartGeneration(context);
        _markup = owner.Markup;
    }

    public override void FinishGeneration()
    {
        _markup = null;
        base.FinishGeneration();
    }

    public override int GetFirstInterestedOffset(int startOffset)
    {
        if (_markup is null)
        {
            return -1;
        }

        var document = CurrentContext.Document;
        var line = document.GetLineByOffset(startOffset);
        var last = CurrentContext.VisualLine.LastDocumentLine.LineNumber;
        while (line is not null)
        {
            foreach (var span in _markup.SpansOnLine(line.LineNumber))
            {
                if (span.Start >= startOffset && Replaces(span) && span.End <= document.TextLength)
                {
                    return span.Start;
                }
            }

            if (line.LineNumber >= last)
            {
                break;
            }

            line = line.NextLine;
        }

        return -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        if (_markup is null)
        {
            return null;
        }

        var line = CurrentContext.Document.GetLineByOffset(offset);
        foreach (var span in _markup.SpansOnLine(line.LineNumber))
        {
            if (span.Start == offset && Replaces(span))
            {
                return owner.CreateElement(span, line.LineNumber);
            }
        }

        return null;
    }

    private bool Replaces(LiveSpan span) => span.IsReplacement && span.Length > 0 && !owner.IsRevealed(span);
}
