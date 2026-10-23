using System.Text;

namespace Ultiviewer.Core.Search;

/// <summary>Query tree. All top-level clauses must hold for a page to match.</summary>
public abstract record QueryNode;

/// <summary>A word or "quoted phrase", matched case- and accent-insensitively.</summary>
public sealed record TermNode(string Text, bool IsPhrase) : QueryNode;

/// <summary><c>#tag</c> (also matches sub-tags like <c>#tag/child</c>).</summary>
public sealed record TagNode(string Tag) : QueryNode;

/// <summary><c>is:todo</c> (Done = false), <c>is:done</c> (true) or <c>is:task</c> (null).</summary>
public sealed record TaskNode(bool? Done) : QueryNode;

public sealed record NotNode(QueryNode Inner) : QueryNode;

public sealed record OrNode(IReadOnlyList<QueryNode> Items) : QueryNode;

public sealed record AndNode(IReadOnlyList<QueryNode> Items) : QueryNode;

/// <summary>
/// Parses the search box: words (all must appear), <c>"exact phrase"</c>, <c>a OR b</c>, <c>-exclude</c>,
/// <c>#tag</c>, <c>is:todo</c> / <c>is:done</c> / <c>is:task</c>.
/// </summary>
public static class SearchQuery
{
    public static AndNode Parse(string text)
    {
        var tokens = Tokenize(text);
        var clauses = new List<QueryNode>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i] is { Kind: TokenKind.Or })
            {
                continue; // stray OR
            }

            var atom = ToNode(tokens[i]);
            var alternatives = new List<QueryNode> { atom };
            while (i + 2 < tokens.Count && tokens[i + 1].Kind == TokenKind.Or && tokens[i + 2].Kind != TokenKind.Or)
            {
                alternatives.Add(ToNode(tokens[i + 2]));
                i += 2;
            }

            clauses.Add(alternatives.Count == 1 ? atom : new OrNode(alternatives));
        }

        return new AndNode(clauses);
    }

    /// <summary>Terms that should be highlighted (everything not under a NOT).</summary>
    public static IEnumerable<TermNode> PositiveTerms(QueryNode node) => node switch
    {
        TermNode term => [term],
        AndNode and => and.Items.SelectMany(PositiveTerms),
        OrNode or => or.Items.SelectMany(PositiveTerms),
        _ => [],
    };

    public static bool IsEmpty(AndNode query) => query.Items.Count == 0;

    private static QueryNode ToNode(Token token)
    {
        QueryNode node = token.Kind switch
        {
            TokenKind.Phrase => new TermNode(token.Text, IsPhrase: true),
            _ when token.Text.StartsWith('#') && token.Text.Length > 1 => new TagNode(token.Text[1..].ToLowerInvariant()),
            _ when token.Text.Equals("is:todo", StringComparison.OrdinalIgnoreCase) => new TaskNode(false),
            _ when token.Text.Equals("is:done", StringComparison.OrdinalIgnoreCase) => new TaskNode(true),
            _ when token.Text.Equals("is:task", StringComparison.OrdinalIgnoreCase) => new TaskNode(null),
            _ => new TermNode(token.Text, IsPhrase: false),
        };
        return token.Negated ? new NotNode(node) : node;
    }

    private enum TokenKind
    {
        Word,
        Phrase,
        Or,
    }

    private sealed record Token(TokenKind Kind, string Text, bool Negated);

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }

            var negated = false;
            if (text[i] == '-' && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]))
            {
                negated = true;
                i++;
            }

            if (text[i] == '"')
            {
                var end = text.IndexOf('"', i + 1);
                var phrase = end < 0 ? text[(i + 1)..] : text[(i + 1)..end];
                i = end < 0 ? text.Length : end + 1;
                phrase = CollapseWhitespace(phrase);
                if (phrase.Length > 0)
                {
                    tokens.Add(new Token(TokenKind.Phrase, phrase, negated));
                }

                continue;
            }

            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            var word = text[start..i];
            tokens.Add(!negated && word == "OR" ? new Token(TokenKind.Or, word, false) : new Token(TokenKind.Word, word, negated));
        }

        return tokens;
    }

    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
