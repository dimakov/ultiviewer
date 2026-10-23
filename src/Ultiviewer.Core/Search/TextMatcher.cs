namespace Ultiviewer.Core.Search;

/// <summary>One highlighted match: 1-based line, 0-based column and length in the original line text.</summary>
public readonly record struct LineMatch(int Line, int Column, int Length);

/// <summary>What a page offers to a query: its title, text lines, tags and tasks.</summary>
public sealed class PageContent
{
    public PageContent(string title, string text)
    {
        Title = title;
        Lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        Facts = PageFacts.Extract(text);
    }

    public string Title { get; }

    public string[] Lines { get; }

    public PageFacts Facts { get; }
}

/// <summary>
/// The final word on whether a page matches (the index only proposes candidates) and where to highlight.
/// Terms of three or more characters match anywhere (substring); shorter terms match at word starts.
/// </summary>
public sealed class TextMatcher
{
    private readonly AndNode _query;
    private readonly IReadOnlyList<string> _positive;

    public TextMatcher(AndNode query)
    {
        _query = query;
        _positive = SearchQuery.PositiveTerms(query).Select(t => TextFolding.Fold(t.Text)).Where(t => t.Length > 0).Distinct().ToList();
    }

    public bool Matches(PageContent page)
    {
        var folded = new FoldedPage(page);
        return _query.Items.Count > 0 && Evaluate(_query, folded);
    }

    /// <summary>Highlight positions of every positive term, line by line, at most <paramref name="max"/>.</summary>
    public IReadOnlyList<LineMatch> FindMatches(PageContent page, int max = 200)
    {
        var result = new List<LineMatch>();
        if (_positive.Count == 0)
        {
            // Tag/task-only queries highlight the lines they matched on.
            foreach (var line in FactLines(_query, page).Distinct().Order())
            {
                result.Add(new LineMatch(line, 0, 0));
            }

            return result;
        }

        for (var i = 0; i < page.Lines.Length && result.Count < max; i++)
        {
            var folded = TextFolding.FoldWithMap(page.Lines[i]);
            var ranges = new List<(int Start, int End)>();
            foreach (var term in _positive)
            {
                foreach (var index in Occurrences(folded.Text, term))
                {
                    var start = folded.Map[index];
                    var endIndex = index + term.Length - 1;
                    var end = folded.Map[Math.Min(endIndex, folded.Map.Length - 1)] + 1;
                    ranges.Add((start, end));
                }
            }

            foreach (var (start, end) in Merge(ranges))
            {
                result.Add(new LineMatch(i + 1, start, end - start));
            }
        }

        return result;
    }

    public bool TitleMatches(string title)
    {
        var folded = TextFolding.Fold(title);
        return _positive.Any(term => Occurrences(folded, term).Any());
    }

    private static bool Evaluate(QueryNode node, FoldedPage page) => node switch
    {
        AndNode and => and.Items.All(item => Evaluate(item, page)),
        OrNode or => or.Items.Any(item => Evaluate(item, page)),
        NotNode not => !Evaluate(not.Inner, page),
        TermNode term => page.Contains(TextFolding.Fold(term.Text)),
        TagNode tag => page.Source.Facts.Tags.Any(t => TagMatches(t.Tag, tag.Tag)),
        TaskNode task => page.Source.Facts.Tasks.Any(t => task.Done is null || t.Done == task.Done),
        _ => false,
    };

    public static bool TagMatches(string pageTag, string queryTag) =>
        pageTag.Equals(queryTag, StringComparison.OrdinalIgnoreCase)
        || pageTag.StartsWith(queryTag + "/", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<int> FactLines(QueryNode node, PageContent page) => node switch
    {
        AndNode and => and.Items.SelectMany(i => FactLines(i, page)),
        OrNode or => or.Items.SelectMany(i => FactLines(i, page)),
        TagNode tag => page.Facts.Tags.Where(t => TagMatches(t.Tag, tag.Tag)).Select(t => t.Line),
        TaskNode task => page.Facts.Tasks.Where(t => task.Done is null || t.Done == task.Done).Select(t => t.Line),
        _ => [],
    };

    /// <summary>Start indexes of <paramref name="term"/> in <paramref name="text"/> under the matching rule.</summary>
    public static IEnumerable<int> Occurrences(string text, string term)
    {
        if (term.Length == 0)
        {
            yield break;
        }

        var wordStartOnly = term.Length < 3;
        var index = text.IndexOf(term, StringComparison.Ordinal);
        while (index >= 0)
        {
            if (!wordStartOnly || index == 0 || !char.IsLetterOrDigit(text[index - 1]))
            {
                yield return index;
            }

            index = text.IndexOf(term, index + 1, StringComparison.Ordinal);
        }
    }

    private static IEnumerable<(int Start, int End)> Merge(List<(int Start, int End)> ranges)
    {
        ranges.Sort();
        (int Start, int End)? current = null;
        foreach (var range in ranges)
        {
            if (current is { } c && range.Start <= c.End)
            {
                current = (c.Start, Math.Max(c.End, range.End));
            }
            else
            {
                if (current is { } done)
                {
                    yield return done;
                }

                current = range;
            }
        }

        if (current is { } last)
        {
            yield return last;
        }
    }

    private sealed class FoldedPage(PageContent page)
    {
        private string? _all;

        public PageContent Source => page;

        public bool Contains(string term) => Occurrences(All, term).Any();

        // Lines joined with '\n' so phrases never match across lines.
        private string All => _all ??= TextFolding.Fold(page.Title + "\n" + string.Join('\n', page.Lines));
    }
}
