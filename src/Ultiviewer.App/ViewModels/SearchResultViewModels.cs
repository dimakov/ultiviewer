using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Ultiviewer.Core.Search;

namespace Ultiviewer.App.ViewModels;

/// <summary>A piece of a result line; <see cref="IsMatch"/> pieces are highlighted.</summary>
public sealed record TextSegment(string Text, bool IsMatch);

/// <summary>One line of a page with all of its matches.</summary>
public sealed class SearchLineViewModel
{
    private const int ContextBefore = 50;
    private const int MaxLength = 220;

    public SearchLineViewModel(string path, int line, string text, IReadOnlyList<(int Column, int Length)> matches)
    {
        Path = path;
        Line = line;
        FirstColumn = matches.Count > 0 ? matches[0].Column : 0;
        FirstLength = matches.Count > 0 ? matches[0].Length : 0;
        Segments = BuildSegments(text, matches);
    }

    public string Path { get; }

    public int Line { get; }

    public int FirstColumn { get; }

    public int FirstLength { get; }

    public IReadOnlyList<TextSegment> Segments { get; }

    /// <summary>Accessible name (screen readers, UI Automation).</summary>
    public override string ToString() => $"Line {Line}: {string.Concat(Segments.Select(s => s.Text))}";

    private static IReadOnlyList<TextSegment> BuildSegments(string text, IReadOnlyList<(int Column, int Length)> matches)
    {
        // Keep some context before the first match; trim leading indentation and very long tails.
        var start = matches.Count > 0 ? Math.Max(0, matches[0].Column - ContextBefore) : 0;
        while (start < text.Length && start < (matches.Count > 0 ? matches[0].Column : text.Length) && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        var end = Math.Min(text.Length, start + MaxLength);
        var segments = new List<TextSegment>();
        if (start > 0)
        {
            segments.Add(new TextSegment("…", false));
        }

        var position = start;
        foreach (var (column, length) in matches)
        {
            if (column < position || column >= end)
            {
                continue;
            }

            if (column > position)
            {
                segments.Add(new TextSegment(text[position..column], false));
            }

            var matchEnd = Math.Min(end, column + length);
            segments.Add(new TextSegment(text[column..matchEnd], true));
            position = matchEnd;
        }

        if (position < end)
        {
            segments.Add(new TextSegment(text[position..end], false));
        }

        if (end < text.Length)
        {
            segments.Add(new TextSegment("…", false));
        }

        return segments;
    }
}

/// <summary>One page in the search results.</summary>
public sealed partial class SearchPageViewModel : ObservableObject
{
    public SearchPageViewModel(PageResult result, string location)
    {
        Path = result.Path;
        Title = result.Title;
        Location = location;
        TitleMatched = result.TitleMatched;
        Lines = result.Hits
            .GroupBy(h => h.Line)
            .Select(g => new SearchLineViewModel(result.Path, g.Key, g.First().LineText, g.Select(h => (h.Column, h.Length)).ToList()))
            .ToList();
        MatchCount = Math.Max(result.Hits.Count, TitleMatched ? 1 : 0);
    }

    public string Path { get; }

    public string Title { get; }

    public string Location { get; }

    public bool TitleMatched { get; }

    public int MatchCount { get; }

    public IReadOnlyList<SearchLineViewModel> Lines { get; }

    public string Extension => System.IO.Path.GetExtension(Path);

    public override string ToString() => $"{Title}, {Location}, {MatchCount} matches";

    [ObservableProperty]
    private bool _isExpanded = true;
}

/// <summary>An entry of the Ctrl+E page switcher.</summary>
public sealed record QuickOpenItem(string Path, string Title, string Location, int Score)
{
    public override string ToString() => $"{Title}, {Location}";
}

public enum SearchScopeOption
{
    AllNotebooks,
    ThisNotebook,
    ThisSection,
    ThisPage,
}

public sealed record SearchScopeChoice(SearchScopeOption Value, string Label)
{
    public override string ToString() => Label;
}
