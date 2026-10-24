using System.Globalization;
using System.Text.RegularExpressions;

namespace Ultiviewer.Core.Markdown;

public enum TaskPriority
{
    Lowest,
    Low,
    Normal,
    Medium,
    High,
    Highest,
}

/// <summary>
/// A task's metadata, written in Obsidian Tasks' emoji format (<c>📅 2026-10-25</c> due, <c>⏳</c> scheduled,
/// <c>🛫</c> start, <c>✅</c> done, <c>🔁 every week</c>, priorities <c>🔺 ⏫ 🔼 🔽 ⏬</c>) or its Dataview format
/// (<c>[due:: 2026-10-25]</c>, <c>[repeat:: every week]</c>, <c>[priority:: high]</c>…), plus reminders as the
/// Reminder plugin writes them (<c>⏰ 2026-10-25 09:00</c>). Notes stay portable to Obsidian.
/// </summary>
public sealed partial record TaskInfo
{
    public string Description { get; init; } = "";

    public DateOnly? Due { get; init; }

    public DateOnly? Scheduled { get; init; }

    public DateOnly? Start { get; init; }

    public DateOnly? Done { get; init; }

    public string? Recurrence { get; init; }

    public TaskPriority Priority { get; init; } = TaskPriority.Normal;

    public DateTime? Reminder { get; init; }

    /// <summary>The date a task is planned for: due, else scheduled, else start.</summary>
    public DateOnly? PlannedDate => Due ?? Scheduled ?? Start;

    /// <summary>Reads the metadata from a task's text (the part after <c>- [ ]</c>).</summary>
    public static TaskInfo Parse(string text)
    {
        DateOnly? due = null, scheduled = null, start = null, done = null;
        string? recurrence = null;
        DateTime? reminder = null;
        var priority = TaskPriority.Normal;

        foreach (Match match in Field().Matches(text))
        {
            var value = match.Groups["value"].Value.Trim();
            switch (Kind(match))
            {
                case "due":
                    due = Date(value) ?? due;
                    break;
                case "scheduled":
                    scheduled = Date(value) ?? scheduled;
                    break;
                case "start":
                    start = Date(value) ?? start;
                    break;
                case "done":
                    done = Date(value) ?? done;
                    break;
                case "repeat":
                    recurrence = value.Length > 0 ? value : recurrence;
                    break;
                case "reminder":
                    reminder = DateTime.TryParseExact(value, ["yyyy-MM-dd HH:mm", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : reminder;
                    break;
                case "priority":
                    priority = PriorityOf(match.Groups["emoji"].Success ? match.Groups["emoji"].Value : value);
                    break;
            }
        }

        var description = Field().Replace(text, " ");
        description = Spaces().Replace(description, " ").Trim();
        return new TaskInfo
        {
            Description = description,
            Due = due,
            Scheduled = scheduled,
            Start = start,
            Done = done,
            Recurrence = recurrence,
            Priority = priority,
            Reminder = reminder,
        };
    }

    private static string Kind(Match match)
    {
        if (match.Groups["emoji"].Success)
        {
            return match.Groups["emoji"].Value switch
            {
                "📅" or "📆" or "🗓" => "due",
                "⏳" or "⌛" => "scheduled",
                "🛫" => "start",
                "✅" => "done",
                "🔁" => "repeat",
                "⏰" => "reminder",
                _ => "priority",
            };
        }

        return match.Groups["key"].Value.ToLowerInvariant() switch
        {
            "due" => "due",
            "scheduled" => "scheduled",
            "start" => "start",
            "completion" or "done" => "done",
            "repeat" or "recurrence" => "repeat",
            "reminder" => "reminder",
            "priority" => "priority",
            _ => "",
        };
    }

    private static TaskPriority PriorityOf(string value) => value.Trim().ToLowerInvariant() switch
    {
        "🔺" or "highest" => TaskPriority.Highest,
        "⏫" or "high" => TaskPriority.High,
        "🔼" or "medium" => TaskPriority.Medium,
        "🔽" or "low" => TaskPriority.Low,
        "⏬" or "lowest" => TaskPriority.Lowest,
        _ => TaskPriority.Normal,
    };

    internal static DateOnly? Date(string value) =>
        DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    /// <summary>
    /// One metadata field: an emoji signifier with its value (up to the next signifier), a bare priority emoji,
    /// or a Dataview <c>[key:: value]</c>.
    /// </summary>
    [GeneratedRegex(@"(?<emoji>📅|📆|🗓|⏳|⌛|🛫|✅|⏰)️?\s*(?<value>\d{4}-\d{2}-\d{2}(?:\s+\d{1,2}:\d{2})?)|(?<emoji>🔁)️?\s*(?<value>[^📅📆🗓⏳⌛🛫✅⏰🔺⏫🔼🔽⏬\[]*)|(?<emoji>🔺|⏫|🔼|🔽|⏬)️?|\[(?<key>due|scheduled|start|completion|done|repeat|recurrence|reminder|priority)::\s*(?<value>[^\]]*)\]", RegexOptions.IgnoreCase)]
    internal static partial Regex Field();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();
}

/// <summary>Recurrence rules in Obsidian Tasks' words: "every day", "every 2 weeks", "every weekday", "every Monday, Friday", "every month on the 15th", "every year", with an optional "when done".</summary>
public static partial class TaskRecurrence
{
    /// <summary>The next date after <paramref name="from"/> for <paramref name="rule"/>, or null when the rule isn't understood.</summary>
    public static DateOnly? Next(string rule, DateOnly from)
    {
        var text = WhenDone().Replace(rule.Trim().ToLowerInvariant(), "").Trim();
        if (!text.StartsWith("every", StringComparison.Ordinal))
        {
            return null;
        }

        text = text[5..].Trim();
        if (text is "weekday" or "weekdays")
        {
            var next = from.AddDays(1);
            while (next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                next = next.AddDays(1);
            }

            return next;
        }

        var weekdays = Weekdays(text.Replace("week on", "", StringComparison.Ordinal));
        if (weekdays.Count > 0)
        {
            var next = from.AddDays(1);
            while (!weekdays.Contains(next.DayOfWeek))
            {
                next = next.AddDays(1);
            }

            return next;
        }

        var match = Interval().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var count = match.Groups["n"].Success ? int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture) : 1;
        var onDay = match.Groups["day"].Success ? match.Groups["day"].Value : null;
        return match.Groups["unit"].Value switch
        {
            "day" or "days" => from.AddDays(count),
            "week" or "weeks" => from.AddDays(7 * count),
            "month" or "months" => MonthDay(from.AddMonths(count), onDay, from.Day),
            "year" or "years" => from.AddYears(count),
            _ => null,
        };
    }

    /// <summary>True for "… when done": the next date counts from completion, not from the old date.</summary>
    public static bool CountsFromCompletion(string rule) => WhenDone().IsMatch(rule.ToLowerInvariant());

    private static DateOnly MonthDay(DateOnly month, string? day, int originalDay)
    {
        var last = DateTime.DaysInMonth(month.Year, month.Month);
        var wanted = day switch
        {
            null => originalDay,
            "last" => last,
            _ => int.Parse(day, CultureInfo.InvariantCulture),
        };
        return new DateOnly(month.Year, month.Month, Math.Min(wanted, last));
    }

    /// <summary>"monday, friday" → those days; empty unless every word is a weekday (full or three-letter name).</summary>
    private static HashSet<DayOfWeek> Weekdays(string text)
    {
        var days = new HashSet<DayOfWeek>();
        foreach (var part in text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part == "and")
            {
                continue;
            }

            DayOfWeek? day = Enum.GetValues<DayOfWeek>()
                .Where(d => d.ToString().Equals(part, StringComparison.OrdinalIgnoreCase) || d.ToString()[..3].Equals(part, StringComparison.OrdinalIgnoreCase))
                .Select(d => (DayOfWeek?)d)
                .FirstOrDefault();
            if (day is null)
            {
                return [];
            }

            days.Add(day.Value);
        }

        return days;
    }

    [GeneratedRegex(@"\s*when done\s*$")]
    private static partial Regex WhenDone();

    [GeneratedRegex(@"^(?:(?<n>\d{1,3})\s+)?(?<unit>days?|weeks?|months?|years?)(?:\s+on\s+the\s+(?:(?<day>\d{1,2})(?:st|nd|rd|th)?|(?<day>last)))?$")]
    private static partial Regex Interval();
}

/// <summary>Completing and reopening task lines, with done dates and recurrence (as Obsidian Tasks does).</summary>
public static partial class TaskCompletion
{
    /// <summary>
    /// Toggles a task line. Completing adds <c>✅ today</c>; a recurring task also gets its next occurrence, an open
    /// copy with every date moved forward, returned first (it goes above the completed one). Reopening removes
    /// the done date. Lines that aren't tasks come back unchanged.
    /// </summary>
    public static IReadOnlyList<string> Toggle(string line, DateOnly today)
    {
        if (!TaskMarkers.IsTask(line, out var done))
        {
            return [line];
        }

        if (done)
        {
            return [RemoveDone(TaskMarkers.Toggle(line))];
        }

        var completed = TaskMarkers.Toggle(line).TrimEnd() + " ✅ " + Format(today);
        var info = TaskInfo.Parse(line);
        if (info.Recurrence is not { } rule)
        {
            return [completed];
        }

        var basis = info.PlannedDate ?? today;
        var reference = TaskRecurrence.CountsFromCompletion(rule) ? today : basis;
        if (TaskRecurrence.Next(rule, reference) is not { } next)
        {
            return [completed];
        }

        var delta = next.DayNumber - basis.DayNumber;
        var nextLine = info.PlannedDate is null
            ? RemoveDone(line).TrimEnd() + " 📅 " + Format(next)
            : Shift(RemoveDone(line), delta);
        return [nextLine, completed];
    }

    /// <summary>
    /// OneNote's Ctrl+1 with dates: plain line → open to-do; open → done (with the done date, and the next
    /// occurrence of a recurring task); done → plain list item (the done date goes too).
    /// </summary>
    public static IReadOnlyList<string> Cycle(string line, DateOnly today)
    {
        if (TaskMarkers.IsTask(line, out var done))
        {
            return done ? [TaskMarkers.Cycle(RemoveDone(line))] : Toggle(line, today);
        }

        return [TaskMarkers.Cycle(line)];
    }

    /// <summary>Sets (or adds) the <c>⏰</c> reminder of a task line.</summary>
    public static string SetReminder(string line, DateTime at)
    {
        var value = at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var replaced = Reminder().Replace(line, "⏰ " + value, 1);
        return replaced != line || Reminder().IsMatch(line) ? replaced : line.TrimEnd() + " ⏰ " + value;
    }

    [GeneratedRegex(@"⏰\uFE0F?\s*\d{4}-\d{2}-\d{2}(?:\s+\d{1,2}:\d{2})?")]
    private static partial Regex Reminder();

    /// <summary>Moves every due, scheduled, start and reminder date by <paramref name="days"/>.</summary>
    public static string Shift(string line, int days) => TaskInfo.Field().Replace(line, match =>
    {
        var kind = match.Groups["emoji"].Success ? match.Groups["emoji"].Value : match.Groups["key"].Value.ToLowerInvariant();
        if (kind is "✅" or "🔁" or "completion" or "done" or "repeat" or "recurrence" or "priority" || !match.Groups["value"].Success)
        {
            return match.Value;
        }

        var value = match.Groups["value"];
        var text = value.Value.Trim();
        string shifted;
        if (DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
        {
            shifted = at.AddDays(days).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
        else if (TaskInfo.Date(text) is { } date)
        {
            shifted = Format(date.AddDays(days));
        }
        else
        {
            return match.Value;
        }

        var start = value.Index - match.Index + (value.Value.Length - value.Value.TrimStart().Length);
        return match.Value[..start] + shifted + match.Value[(start + text.Length)..];
    });

    private static string RemoveDone(string line) => DoneField().Replace(line, "").TrimEnd();

    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\s*(?:✅\uFE0F?\s*\d{4}-\d{2}-\d{2}|\[(?:completion|done)::[^\]]*\])")]
    private static partial Regex DoneField();
}
