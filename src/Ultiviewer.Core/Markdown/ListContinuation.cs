using System.Globalization;
using System.Text.RegularExpressions;

namespace Ultiviewer.Core.Markdown;

/// <summary>What pressing Enter at the end of a line should do in a Markdown document.</summary>
public readonly record struct EnterAction(string? NextPrefix, bool EndsList);

/// <summary>
/// Continues lists and quotes on Enter: <c>- item</c> → <c>- </c>, <c>3. item</c> → <c>4. </c>,
/// <c>- [x] task</c> → <c>- [ ] </c>, <c>&gt; quote</c> → <c>&gt; </c>. Enter on an empty item ends the list.
/// </summary>
public static partial class ListContinuation
{
    public static EnterAction OnEnter(string line)
    {
        var item = ListItem().Match(line);
        if (item.Success)
        {
            if (item.Groups["rest"].Value.Trim().Length == 0)
            {
                return new EnterAction(null, EndsList: true);
            }

            var marker = item.Groups["num"].Success
                ? (long.Parse(item.Groups["num"].Value, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture) + item.Groups["delim"].Value
                : item.Groups["bullet"].Value;
            var task = item.Groups["task"].Success ? "[ ] " : "";
            return new EnterAction(item.Groups["indent"].Value + marker + item.Groups["space"].Value + task, EndsList: false);
        }

        var quote = Quote().Match(line);
        if (quote.Success)
        {
            return quote.Groups["rest"].Value.Trim().Length == 0
                ? new EnterAction(null, EndsList: true)
                : new EnterAction(quote.Groups["prefix"].Value, EndsList: false);
        }

        return new EnterAction(null, EndsList: false);
    }

    [GeneratedRegex(@"^(?<indent>[ \t]*)(?:(?<bullet>[-*+])|(?<num>\d{1,9})(?<delim>[.)]))(?<space>[ \t]+)(?<task>\[[ xX]\][ \t]+)?(?<rest>.*)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^(?<prefix>[ \t]*(?:>[ \t]?)+)(?<rest>.*)$")]
    private static partial Regex Quote();
}
