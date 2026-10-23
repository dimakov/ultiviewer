using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Rendering;
using Ultiviewer.App.Editor;
using Ultiviewer.Core.Text;

namespace Ultiviewer.App.Views;

/// <summary>
/// Find / replace bar above the editor (Ctrl+F / Ctrl+H). Highlights every match, shows "n of m", searches
/// incrementally while typing, supports match case, whole word, regex (with $1 replacements) and in-selection.
/// </summary>
public partial class FindBar : UserControl
{
    private readonly TextEditor _editor;
    private readonly SearchHighlighter _highlighter;
    private readonly DispatcherTimer _refresh;
    private Regex? _regex;
    private IReadOnlyList<TextRange> _matches = [];
    private TextRange? _scope;

    public FindBar(TextEditor editor, SearchHighlighter highlighter)
    {
        InitializeComponent();
        _editor = editor;
        _highlighter = highlighter;
        _refresh = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) =>
        {
            _refresh!.Stop();
            Recompute(selectNearest: false);
        }, Dispatcher);
        _refresh.Stop();
        Visibility = Visibility.Collapsed;
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>The current search, if the bar is open and has a valid pattern.</summary>
    public string Pattern => FindBox.Text;

    public event Action? Closed;

    private FindOptions Options => new(MatchCase.IsChecked == true, WholeWord.IsChecked == true, UseRegex.IsChecked == true);

    public void Open(bool replace)
    {
        var selected = _editor.SelectedText;
        if (selected.Length > 0 && !selected.Contains('\n'))
        {
            FindBox.Text = selected;
            InSelection.IsChecked = false;
            _scope = null;
        }
        else if (selected.Contains('\n'))
        {
            InSelection.IsChecked = true;
            _scope = new TextRange(_editor.SelectionStart, _editor.SelectionLength);
        }

        Visibility = Visibility.Visible;
        ShowReplace(replace || ReplaceToggle.IsChecked == true);
        Dispatcher.BeginInvoke(() =>
        {
            FindBox.Focus();
            FindBox.SelectAll();
        }, DispatcherPriority.Input);
        Recompute(selectNearest: false);
    }

    public void Close()
    {
        Visibility = Visibility.Collapsed;
        _matches = [];
        _highlighter.SetMatches([]);
        Closed?.Invoke();
    }

    /// <summary>Re-runs the search after the document changed or another tab was shown.</summary>
    public void DocumentChanged()
    {
        if (IsOpen)
        {
            _refresh.Stop();
            _refresh.Start();
        }
    }

    public void FindNext() => Step(forward: true);

    public void FindPrevious() => Step(forward: false);

    private void ShowReplace(bool show)
    {
        ReplaceToggle.IsChecked = show;
        ReplaceBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ReplaceButtons.Visibility = ReplaceBox.Visibility;
    }

    private void Recompute(bool selectNearest)
    {
        var pattern = FindBox.Text;
        _regex = null;
        _matches = [];
        FindBox.ClearValue(BorderBrushProperty);
        FindBox.ToolTip = null;
        if (pattern.Length > 0)
        {
            try
            {
                _regex = TextSearcher.Build(pattern, Options);
            }
            catch (ArgumentException ex)
            {
                FindBox.BorderBrush = Brushes.IndianRed;
                FindBox.ToolTip = ex.Message;
            }
        }

        if (_regex is not null)
        {
            var text = _editor.Document.Text;
            var scope = InSelection.IsChecked == true && _scope is { } s && s.End <= text.Length ? s : new TextRange(0, text.Length);
            _matches = TextSearcher.FindAll(text, _regex, scope.Start, scope.End);
        }

        _highlighter.SetMatches(_matches);
        if (selectNearest && _matches.Count > 0)
        {
            var caret = _editor.SelectionStart;
            Select(_matches.FirstOrDefault(m => m.Start >= caret, _matches[0]));
        }

        UpdateCount();
    }

    private void UpdateCount()
    {
        if (_regex is null)
        {
            CountText.Text = FindBox.Text.Length == 0 ? "" : "Invalid pattern";
            return;
        }

        if (_matches.Count == 0)
        {
            CountText.Text = "No matches";
            return;
        }

        var current = CurrentIndex();
        var total = _matches.Count >= TextSearcher.MaxMatches ? $"{_matches.Count}+" : _matches.Count.ToString();
        CountText.Text = current >= 0 ? $"{current + 1} of {total}" : $"{total} matches";
    }

    private int CurrentIndex()
    {
        var start = _editor.SelectionStart;
        var length = _editor.SelectionLength;
        for (var i = 0; i < _matches.Count; i++)
        {
            if (_matches[i].Start == start && _matches[i].Length == length)
            {
                return i;
            }
        }

        return -1;
    }

    private void Step(bool forward)
    {
        if (_matches.Count == 0)
        {
            Recompute(selectNearest: false);
            if (_matches.Count == 0)
            {
                return;
            }
        }

        var current = CurrentIndex();
        TextRange target;
        if (forward)
        {
            var from = current >= 0 ? _matches[current].End : _editor.CaretOffset;
            target = _matches.FirstOrDefault(m => m.Start >= from && (current < 0 || m.Start > _matches[current].Start), _matches[0]);
        }
        else
        {
            var from = current >= 0 ? _matches[current].Start : _editor.CaretOffset;
            target = _matches.LastOrDefault(m => m.Start < from, _matches[^1]);
        }

        Select(target);
        UpdateCount();
    }

    private void Select(TextRange range)
    {
        _editor.Select(range.Start, range.Length);
        var location = _editor.Document.GetLocation(range.Start);
        _editor.ScrollTo(location.Line, location.Column, VisualYPosition.LineMiddle, _editor.TextArea.TextView.ActualHeight / 2, 0.3);
    }

    private void ReplaceOne()
    {
        if (_regex is null)
        {
            return;
        }

        var current = CurrentIndex();
        if (current < 0)
        {
            Step(forward: true);
            return;
        }

        var match = _matches[current];
        var replacement = TextSearcher.ReplacementFor(_editor.Document.Text, match, _regex, ReplaceBox.Text, Options);
        _editor.Document.Replace(match.Start, match.Length, replacement);
        _editor.CaretOffset = match.Start + replacement.Length;
        Recompute(selectNearest: false);
        var next = _matches.FirstOrDefault(m => m.Start >= match.Start + replacement.Length, _matches.Count > 0 ? _matches[0] : default);
        if (_matches.Count > 0)
        {
            Select(next);
        }

        UpdateCount();
    }

    private void ReplaceAll()
    {
        if (_regex is null || _matches.Count == 0)
        {
            return;
        }

        var text = _editor.Document.Text;
        var replacements = _matches.Select(m => (m, TextSearcher.ReplacementFor(text, m, _regex, ReplaceBox.Text, Options))).ToList();
        using (_editor.Document.RunUpdate())
        {
            for (var i = replacements.Count - 1; i >= 0; i--)
            {
                var (match, replacement) = replacements[i];
                _editor.Document.Replace(match.Start, match.Length, replacement);
            }
        }

        Recompute(selectNearest: false);
        CountText.Text = $"Replaced {replacements.Count}";
    }

    private void OnFindTextChanged(object sender, TextChangedEventArgs e) => Recompute(selectNearest: true);

    private void OnOptionChanged(object sender, RoutedEventArgs e) => Recompute(selectNearest: true);

    private void OnInSelectionChanged(object sender, RoutedEventArgs e)
    {
        _scope = InSelection.IsChecked == true && _editor.SelectionLength > 0
            ? new TextRange(_editor.SelectionStart, _editor.SelectionLength)
            : null;
        if (_scope is null)
        {
            InSelection.IsChecked = false;
        }

        Recompute(selectNearest: false);
    }

    private void OnReplaceToggle(object sender, RoutedEventArgs e) => ShowReplace(ReplaceToggle.IsChecked == true);

    private void OnFindKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Step(forward: Keyboard.Modifiers != ModifierKeys.Shift);
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.F3:
                Step(forward: Keyboard.Modifiers != ModifierKeys.Shift);
                e.Handled = true;
                break;
        }
    }

    private void OnReplaceKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when Keyboard.Modifiers == ModifierKeys.Control:
                ReplaceAll();
                e.Handled = true;
                break;
            case Key.Enter:
                ReplaceOne();
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    private void OnPrevious(object sender, RoutedEventArgs e) => Step(forward: false);

    private void OnNext(object sender, RoutedEventArgs e) => Step(forward: true);

    private void OnReplaceOne(object sender, RoutedEventArgs e) => ReplaceOne();

    private void OnReplaceAll(object sender, RoutedEventArgs e) => ReplaceAll();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
