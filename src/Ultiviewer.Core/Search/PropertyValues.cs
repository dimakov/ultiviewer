using System.Globalization;
using System.Text.RegularExpressions;

namespace Ultiviewer.Core.Search;

/// <summary>
/// How property values compare, for queries and for sorting result tables: numbers numerically, everything else
/// as case- and accent-insensitive text (ISO dates such as 2026-10-24 sort correctly as text).
/// </summary>
public static class PropertyValues
{
    public static bool Matches(PropertyNode node, string value) => node.Op switch
    {
        PropertyOp.Exists => true,
        PropertyOp.Equals when node.Value.Contains('*') => Wildcard(Fold(node.Value)).IsMatch(Fold(value)),
        PropertyOp.Equals => Compare(value, node.Value) == 0,
        PropertyOp.Less => Compare(value, node.Value) < 0,
        PropertyOp.LessOrEqual => Compare(value, node.Value) <= 0,
        PropertyOp.Greater => Compare(value, node.Value) > 0,
        PropertyOp.GreaterOrEqual => Compare(value, node.Value) >= 0,
        _ => false,
    };

    public static int Compare(string a, string b) =>
        TryNumber(a, out var x) && TryNumber(b, out var y) ? x.CompareTo(y) : string.CompareOrdinal(Fold(a), Fold(b));

    /// <summary>For sorting: empty values last, then <see cref="Compare"/>.</summary>
    public static int CompareForSort(string? a, string? b) => (string.IsNullOrWhiteSpace(a), string.IsNullOrWhiteSpace(b)) switch
    {
        (true, true) => 0,
        (true, false) => 1,
        (false, true) => -1,
        _ => Compare(a!, b!),
    };

    public static bool TryNumber(string text, out double number) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);

    /// <summary>The form values are stored and compared in (trimmed, folded).</summary>
    public static string Fold(string value) => TextFolding.Fold(value.Trim());

    private static Regex Wildcard(string pattern) =>
        new("^" + string.Join(".*", pattern.Split('*').Select(Regex.Escape)) + "$", RegexOptions.Singleline | RegexOptions.CultureInvariant);
}
