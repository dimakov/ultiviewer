using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Folding;
using Ultiviewer.App.Editor;
using Ultiviewer.App.Services;
using Ultiviewer.App.ViewModels;
using Ultiviewer.Core.Markdown;

namespace Ultiviewer.App.Views;

/// <summary>
/// Folding for Markdown: heading sections, code blocks, front matter and list items with children (ranges from
/// <see cref="MarkdownOutline.Folds"/>). AvalonEdit's FoldingManager belongs to one document, so it is reinstalled
/// whenever the editor shows another tab; folded regions are remembered per tab and per editor, and in the tab
/// for the session.
/// </summary>
public partial class EditorHost
{
    private FoldingManager? _foldingManager;
    private DispatcherTimer? _foldTimer;
    private readonly Dictionary<DocumentTabViewModel, IReadOnlyList<int>> _foldStates = [];

    private void InitializeFolding()
    {
        _foldTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) =>
        {
            _foldTimer!.Stop();
            UpdateFoldings();
        }, Dispatcher);
        _foldTimer.Stop();
        Editor.TextChanged += (_, _) =>
        {
            if (_foldingManager is not null)
            {
                _foldTimer.Stop();
                _foldTimer.Start();
            }
        };

        Bind(EditorCommands.Fold, (_, _) => FoldAtCaret(fold: true));
        Bind(EditorCommands.Unfold, (_, _) => FoldAtCaret(fold: false));
        Bind(EditorCommands.FoldAll, (_, _) => SetAllFolds(fold: true));
        Bind(EditorCommands.UnfoldAll, (_, _) => SetAllFolds(fold: false));
    }

    /// <summary>Remembers which regions of the current tab are folded (before switching away or saving the session).</summary>
    private void CaptureFolds()
    {
        if (_current is null || _foldingManager is null)
        {
            return;
        }

        var folded = _foldingManager.AllFoldings
            .Where(f => f.IsFolded)
            .Select(f => Editor.Document.GetLineByOffset(f.StartOffset).LineNumber)
            .ToList();
        _foldStates[_current] = folded;
        _current.FoldedLines = folded;
    }

    /// <summary>Sets up folding for the tab now shown (Markdown only) and folds what was folded before.</summary>
    private void InstallFolding()
    {
        if (_foldingManager is not null)
        {
            FoldingManager.Uninstall(_foldingManager);
            _foldingManager = null;
        }

        if (_current is not { IsMarkdown: true } tab)
        {
            return;
        }

        _foldingManager = FoldingManager.Install(Editor.TextArea);
        StyleFoldingMargin();
        UpdateFoldings();
        var folded = _foldStates.TryGetValue(tab, out var own) ? own : tab.FoldedLines;
        foreach (var section in _foldingManager.AllFoldings)
        {
            if (folded.Contains(Editor.Document.GetLineByOffset(section.StartOffset).LineNumber))
            {
                section.IsFolded = true;
            }
        }
    }

    /// <summary>Quiet fold markers in the line-number color, so they don't compete with the text.</summary>
    private void StyleFoldingMargin()
    {
        var faint = ThemeService.Instance.Palette.LineNumbers;
        foreach (var margin in Editor.TextArea.LeftMargins.OfType<FoldingMargin>())
        {
            margin.FoldingMarkerBrush = new SolidColorBrush(Color.FromArgb(0x90, faint.R, faint.G, faint.B));
            margin.FoldingMarkerBackgroundBrush = Brushes.Transparent;
            margin.SelectedFoldingMarkerBrush = new SolidColorBrush(faint);
            margin.SelectedFoldingMarkerBackgroundBrush = Brushes.Transparent;
        }
    }

    private void UpdateFoldings()
    {
        if (_foldingManager is null)
        {
            return;
        }

        var document = Editor.Document;
        var folds = MarkdownOutline.Folds(document.Text)
            .Where(f => f.EndLine < document.LineCount)
            .Select(f => new NewFolding(document.GetLineByNumber(f.StartLine + 1).EndOffset, document.GetLineByNumber(f.EndLine + 1).EndOffset) { Name = " ⋯ " })
            .Where(f => f.EndOffset > f.StartOffset)
            .OrderBy(f => f.StartOffset)
            .ToList();
        _foldingManager.UpdateFoldings(folds, -1);
    }

    /// <summary>Folds the innermost open region around the caret line, or unfolds the folded one there.</summary>
    private void FoldAtCaret(bool fold)
    {
        if (_foldingManager is null)
        {
            return;
        }

        UpdateFoldings(); // ranges may lag behind the last edit by the debounce delay
        var line = Editor.Document.GetLineByOffset(Editor.CaretOffset);
        var containing = _foldingManager.AllFoldings
            .Where(f => f.StartOffset <= line.EndOffset && f.EndOffset >= line.Offset && f.IsFolded != fold)
            .OrderByDescending(f => f.StartOffset)
            .FirstOrDefault();
        if (containing is not null)
        {
            containing.IsFolded = fold;
            if (fold)
            {
                Editor.CaretOffset = Editor.Document.GetLineByOffset(containing.StartOffset).EndOffset;
            }
        }
    }

    /// <summary>
    /// Folds every heading section (code and lists stay open), or unfolds everything. A page's single top-level
    /// heading (its title) stays open, or folding would hide the whole page.
    /// </summary>
    private void SetAllFolds(bool fold)
    {
        if (_foldingManager is null)
        {
            return;
        }

        UpdateFoldings();
        var text = Editor.Document.Text;
        var headings = MarkdownOutline.Headings(text);
        var title = headings.Count > 0 && headings.Count(h => h.Level == headings.Min(m => m.Level)) == 1
            ? headings.First(h => h.Level == headings.Min(m => m.Level)).Line
            : -1;
        var sections = MarkdownOutline.Folds(text).Where(f => f.Kind == FoldKind.Section && f.StartLine != title).Select(f => f.StartLine + 1).ToHashSet();
        foreach (var section in _foldingManager.AllFoldings)
        {
            if (!fold)
            {
                section.IsFolded = false;
            }
            else if (sections.Contains(Editor.Document.GetLineByOffset(section.StartOffset).LineNumber))
            {
                section.IsFolded = true;
            }
        }

        if (fold)
        {
            Editor.CaretOffset = Editor.Document.GetLineByOffset(Editor.CaretOffset).Offset;
        }
    }
}
