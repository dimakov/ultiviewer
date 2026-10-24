using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Ultiviewer.Core.Markdown;

namespace Ultiviewer.App.ViewModels;

/// <summary>A heading in the Outline pane.</summary>
public sealed partial class OutlineItem : ObservableObject
{
    public required string Text { get; init; }

    public required int Level { get; init; }

    /// <summary>1-based line of the heading.</summary>
    public required int Line { get; init; }

    public ObservableCollection<OutlineItem> Children { get; } = [];

    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>The heading whose section holds the caret.</summary>
    [ObservableProperty]
    private bool _isCurrent;

    public string Key => $"{Level}:{Text}";
}

/// <summary>The Outline pane: the active page's headings as a tree, kept current while typing.</summary>
public partial class MainViewModel
{
    private DispatcherTimer? _outlineTimer;
    private readonly HashSet<string> _collapsedOutline = [];
    private List<OutlineItem> _outlineFlat = [];

    public ObservableCollection<OutlineItem> Outline { get; } = [];

    [ObservableProperty]
    private string _outlineMessage = "Open a Markdown page to see its headings.";

    private void InitializeOutline()
    {
        _outlineTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background, (_, _) =>
        {
            _outlineTimer!.Stop();
            RefreshOutline();
        }, _dispatcher);
        _outlineTimer.Stop();
        DocumentEdited += tab =>
        {
            if (tab == ActiveTab)
            {
                _outlineTimer.Stop();
                _outlineTimer.Start();
            }
        };
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ActiveTab))
            {
                RefreshOutline();
            }
            else if (e.PropertyName == nameof(CaretLine))
            {
                MarkCurrentHeading();
            }
        };
        RefreshOutline();
    }

    /// <summary>Rebuilds the tree from the active page; collapsed headings stay collapsed.</summary>
    public void RefreshOutline()
    {
        foreach (var item in _outlineFlat.Where(i => !i.IsExpanded))
        {
            _collapsedOutline.Add(item.Key);
        }

        foreach (var item in _outlineFlat.Where(i => i.IsExpanded))
        {
            _collapsedOutline.Remove(item.Key);
        }

        Outline.Clear();
        _outlineFlat = [];
        if (ActiveTab is not { IsMarkdown: true } tab)
        {
            OutlineMessage = "Open a Markdown page to see its headings.";
            return;
        }

        var parents = new Stack<OutlineItem>();
        foreach (var heading in MarkdownOutline.Headings(tab.Document.Text))
        {
            var item = new OutlineItem { Text = heading.Text.Length > 0 ? heading.Text : "(empty heading)", Level = heading.Level, Line = heading.Line + 1 };
            item.IsExpanded = !_collapsedOutline.Contains(item.Key);
            while (parents.Count > 0 && parents.Peek().Level >= heading.Level)
            {
                parents.Pop();
            }

            (parents.Count > 0 ? parents.Peek().Children : Outline).Add(item);
            parents.Push(item);
            _outlineFlat.Add(item);
        }

        OutlineMessage = _outlineFlat.Count == 0 ? "This page has no headings yet. Lines starting with # appear here." : "";
        MarkCurrentHeading();
    }

    private void MarkCurrentHeading()
    {
        var current = _outlineFlat.LastOrDefault(i => i.Line <= CaretLine);
        foreach (var item in _outlineFlat)
        {
            item.IsCurrent = item == current;
        }
    }

    /// <summary>Moves the caret to a heading (from the Outline pane).</summary>
    public void GoToOutlineItem(OutlineItem item)
    {
        GoToLine(item.Line);
        FocusEditor();
    }
}
