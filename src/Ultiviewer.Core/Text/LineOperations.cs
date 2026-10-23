using System.Globalization;
using Ultiviewer.Core.Notebooks;

namespace Ultiviewer.Core.Text;

/// <summary>Notepad++-style operations on a block of lines (the selected lines, or the whole document).</summary>
public static class LineOperations
{
    public static IReadOnlyList<string> SortAscending(IReadOnlyList<string> lines) =>
        lines.Order(NaturalStringComparer.Instance).ToList();

    public static IReadOnlyList<string> SortDescending(IReadOnlyList<string> lines) =>
        lines.OrderDescending(NaturalStringComparer.Instance).ToList();

    public static IReadOnlyList<string> RemoveDuplicates(IReadOnlyList<string> lines)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return lines.Where(seen.Add).ToList();
    }

    public static IReadOnlyList<string> RemoveEmpty(IReadOnlyList<string> lines) =>
        lines.Where(l => l.Trim().Length > 0).ToList();

    public static IReadOnlyList<string> TrimTrailing(IReadOnlyList<string> lines) =>
        lines.Select(l => l.TrimEnd(' ', '\t')).ToList();

    /// <summary>Joins lines into one, separated by single spaces (leading indentation of later lines dropped).</summary>
    public static string Join(IReadOnlyList<string> lines) =>
        string.Join(" ", lines.Select((l, i) => i == 0 ? l.TrimEnd() : l.Trim()).Where((l, i) => i == 0 || l.Length > 0));

    public static string ToUpper(string text) => text.ToUpper(CultureInfo.CurrentCulture);

    public static string ToLower(string text) => text.ToLower(CultureInfo.CurrentCulture);

    /// <summary>Title Case Every Word.</summary>
    public static string ToTitleCase(string text) => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(text.ToLower(CultureInfo.CurrentCulture));
}
