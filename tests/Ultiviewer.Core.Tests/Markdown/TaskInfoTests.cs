using Ultiviewer.Core.Markdown;

namespace Ultiviewer.Core.Tests.Markdown;

public class TaskInfoTests
{
    private static readonly DateOnly Today = new(2026, 10, 24); // a Saturday

    [Fact]
    public void ParsesEmojiMetadata()
    {
        var info = TaskInfo.Parse("- [ ] Pay rent ⏫ 🔁 every month on the 1st 📅 2026-11-01 ⏳ 2026-10-30 ⏰ 2026-10-31 09:00");

        Assert.Equal("- [ ] Pay rent", info.Description);
        Assert.Equal(new DateOnly(2026, 11, 1), info.Due);
        Assert.Equal(new DateOnly(2026, 10, 30), info.Scheduled);
        Assert.Equal("every month on the 1st", info.Recurrence);
        Assert.Equal(TaskPriority.High, info.Priority);
        Assert.Equal(new DateTime(2026, 10, 31, 9, 0, 0), info.Reminder);
    }

    [Fact]
    public void ParsesDataviewMetadata()
    {
        var info = TaskInfo.Parse("Call [due:: 2026-10-25] [priority:: low] [repeat:: every week]");

        Assert.Equal(("Call", new DateOnly(2026, 10, 25), TaskPriority.Low, "every week"), (info.Description, info.Due, info.Priority, info.Recurrence));
    }

    [Theory]
    [InlineData("every day", "2026-10-25")]
    [InlineData("every 3 days", "2026-10-27")]
    [InlineData("every week", "2026-10-31")]
    [InlineData("every 2 weeks", "2026-11-07")]
    [InlineData("every weekday", "2026-10-26")]
    [InlineData("every Monday, Thursday", "2026-10-26")]
    [InlineData("every week on Friday", "2026-10-30")]
    [InlineData("every month", "2026-11-24")]
    [InlineData("every month on the 31st", "2026-11-30")]
    [InlineData("every month on the last", "2026-11-30")]
    [InlineData("every year", "2027-10-24")]
    [InlineData("every week when done", "2026-10-31")]
    public void ComputesNextDates(string rule, string expected) =>
        Assert.Equal(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), TaskRecurrence.Next(rule, Today));

    [Fact]
    public void RejectsUnknownRules() => Assert.Null(TaskRecurrence.Next("sometimes", Today));

    [Fact]
    public void CompletingAddsTheDoneDateAndReopeningRemovesIt()
    {
        var done = Assert.Single(TaskCompletion.Toggle("- [ ] Write report 📅 2026-10-24", Today));
        Assert.Equal("- [x] Write report 📅 2026-10-24 ✅ 2026-10-24", done);
        Assert.Equal("- [ ] Write report 📅 2026-10-24", Assert.Single(TaskCompletion.Toggle(done, Today)));
        Assert.Equal("plain line", Assert.Single(TaskCompletion.Toggle("plain line", Today)));
    }

    [Fact]
    public void CompletingARecurringTaskCreatesTheNextOne()
    {
        var lines = TaskCompletion.Toggle("  - [ ] Water plants 🔁 every week ⏳ 2026-10-22 📅 2026-10-23 ⏰ 2026-10-23 08:00", Today);

        Assert.Equal(
            ["  - [ ] Water plants 🔁 every week ⏳ 2026-10-29 📅 2026-10-30 ⏰ 2026-10-30 08:00",
             "  - [x] Water plants 🔁 every week ⏳ 2026-10-22 📅 2026-10-23 ⏰ 2026-10-23 08:00 ✅ 2026-10-24"],
            lines);
    }

    [Fact]
    public void CtrlOneCyclesWithDates()
    {
        Assert.Equal(["- [ ] call"], TaskCompletion.Cycle("call", Today));
        Assert.Equal(["- [x] call ✅ 2026-10-24"], TaskCompletion.Cycle("- [ ] call", Today));
        Assert.Equal(["- call"], TaskCompletion.Cycle("- [x] call ✅ 2026-10-24", Today));
    }

    [Fact]
    public void SetsOrReplacesReminders()
    {
        var at = new DateTime(2026, 10, 24, 15, 30, 0);

        Assert.Equal("- [ ] x ⏰ 2026-10-24 15:30", TaskCompletion.SetReminder("- [ ] x", at));
        Assert.Equal("- [ ] x ⏰ 2026-10-24 15:30 📅 2026-10-25", TaskCompletion.SetReminder("- [ ] x ⏰ 2026-10-20 09:00 📅 2026-10-25", at));
    }

    [Fact]
    public void WhenDoneCountsFromCompletionAndUndatedTasksGetADueDate()
    {
        Assert.Equal("- [ ] Haircut 🔁 every 4 weeks when done 📅 2026-11-21", TaskCompletion.Toggle("- [ ] Haircut 🔁 every 4 weeks when done 📅 2026-09-01", Today)[0]);
        Assert.Equal("- [ ] Stretch 🔁 every day 📅 2026-10-25", TaskCompletion.Toggle("- [ ] Stretch 🔁 every day", Today)[0]);
    }
}
