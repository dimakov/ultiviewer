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

public enum PropertyOp
{
    /// <summary><c>key:*</c>: the page has the property.</summary>
    Exists,

    /// <summary><c>key:value</c> (<c>*</c> is a wildcard).</summary>
    Equals,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
}

/// <summary><c>status:done</c>, <c>priority:&gt;=2</c>, <c>due:&lt;2026-11-01</c>, <c>project:*</c>: a page property (front matter or <c>key:: value</c>).</summary>
public sealed record PropertyNode(string Key, PropertyOp Op, string Value) : QueryNode;

public sealed record NotNode(QueryNode Inner) : QueryNode;

public sealed record OrNode(IReadOnlyList<QueryNode> Items) : QueryNode;

public sealed record AndNode(IReadOnlyList<QueryNode> Items) : QueryNode;

/// <summary>
/// Parses the search box: words (all must appear), <c>"exact phrase"</c>, <c>a OR b</c>, <c>-exclude</c>,
/// <c>#tag</c>, <c>is:todo</c> / <c>is:done</c> / <c>is:task</c>, and properties <c>key:value</c> with
/// <c>&lt; &lt;= &gt; &gt;=</c>, <c>key:*</c> and <c>key:"two words"</c>. A quoted <c>"key:value"</c> stays text.
/// </summary>
public static partial class SearchQuery
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

    /// <summary>Property keys the query mentions, in order (for result table columns).</summary>
    public static IEnumerable<string> PropertyKeys(QueryNode node) => node switch
    {
        PropertyNode property => [property.Key],
        AndNode and => and.Items.SelectMany(PropertyKeys),
        OrNode or => or.Items.SelectMany(PropertyKeys),
        NotNode not => PropertyKeys(not.Inner),
        _ => [],
    };

    private static QueryNode ToNode(Token token)
    {
        QueryNode node = token.Kind switch
        {
            TokenKind.Phrase => new TermNode(token.Text, IsPhrase: true),
            _ when token.Text.StartsWith('#') && token.Text.Length > 1 => new TagNode(token.Text[1..].ToLowerInvariant()),
            _ when token.Text.Equals("is:todo", StringComparison.OrdinalIgnoreCase) => new TaskNode(false),
            _ when token.Text.Equals("is:done", StringComparison.OrdinalIgnoreCase) => new TaskNode(true),
            _ when token.Text.Equals("is:task", StringComparison.OrdinalIgnoreCase) => new TaskNode(null),
            _ when Property(token.Text) is { } property => property,
            _ => new TermNode(token.Text, IsPhrase: false),
        };
        return token.Negated ? new NotNode(node) : node;
    }

    /// <summary>
    /// <c>key:value</c> with a key that starts with a letter; values starting with <c>/</c> or <c>\</c> (URLs,
    /// paths) and <c>is:…</c> stay plain words, and so does <c>key:</c> with nothing after it ("TODO:").
    /// </summary>
    private static PropertyNode? Property(string word)
    {
        var match = PropertyToken().Match(word);
        if (!match.Success || match.Groups["key"].Value.Equals("is", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var op = match.Groups["op"].Value switch
        {
            "<" => PropertyOp.Less,
            "<=" => PropertyOp.LessOrEqual,
            ">" => PropertyOp.Greater,
            ">=" => PropertyOp.GreaterOrEqual,
            _ => PropertyOp.Equals,
        };
        var value = match.Groups["value"].Value;
        if (value.StartsWith('"'))
        {
            value = CollapseWhitespace(value.Trim('"'));
        }

        if (value.Length == 0 || value[0] is '/' or '\\')
        {
            return null;
        }

        return new PropertyNode(match.Groups["key"].Value.ToLowerInvariant(), op == PropertyOp.Equals && value == "*" ? PropertyOp.Exists : op, value);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(?<key>[\p{L}_][\p{L}\p{N}_\-]*):(?<op><=|>=|<|>|=)?(?<value>.+)$")]
    private static partial System.Text.RegularExpressions.Regex PropertyToken();

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
                // key:"two words" keeps its quoted value in one word.
                if (text[i] == '"' && i > start && text[i - 1] is ':' or '=' or '<' or '>')
                {
                    var close = text.IndexOf('"', i + 1);
                    i = close < 0 ? text.Length : close + 1;
                    continue;
                }

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
