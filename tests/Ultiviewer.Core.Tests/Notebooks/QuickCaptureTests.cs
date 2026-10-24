using Ultiviewer.Core.Capture;
using Ultiviewer.Core.Notebooks;

namespace Ultiviewer.Core.Tests.Notebooks;

public class QuickCaptureTests
{
    private static readonly DateTime Now = new(2026, 10, 24, 14, 5, 0);

    [Fact]
    public void AppendsTimeStampedNotes()
    {
        var once = QuickCapture.Append("# Today\n", "Call Dana", Now, "HH:mm", "\n");
        var twice = QuickCapture.Append(once, "Buy milk", Now.AddMinutes(10), "HH:mm", "\n");
        var longer = QuickCapture.Append(twice, "Idea:\nsplit the editor", Now.AddHours(1), "HH:mm", "\n");

        Assert.Equal("# Today\n\n- 14:05 Call Dana\n", once);
        Assert.Equal("# Today\n\n- 14:05 Call Dana\n- 14:15 Buy milk\n", twice);
        Assert.Equal("# Today\n\n- 14:05 Call Dana\n- 14:15 Buy milk\n\n**15:05**\nIdea:\nsplit the editor\n", longer);
        Assert.Equal("- 14:05 x\r\n", QuickCapture.Append("", " x ", Now, "HH:mm", "\r\n"));
        Assert.Equal("keep", QuickCapture.Append("keep", "  ", Now, "HH:mm", "\n"));
    }

    [Fact]
    public void DailyNotePathsAndDefaultText()
    {
        Assert.Equal(Path.Combine("C:\\nb", "Daily", "2026-10-24.md"), DailyNotes.PathFor("C:\\nb", "Daily", "yyyy-MM-dd", Now));
        Assert.Equal(Path.Combine("C:\\nb", "2026-10-24.md"), DailyNotes.PathFor("C:\\nb", "", "", Now));
        Assert.Equal(Path.Combine("C:\\nb", "Daily", "24-10-2026 Sat.md"), DailyNotes.PathFor("C:\\nb", "Daily", "dd/MM/yyyy ddd", Now).Replace("24-10-2026", "24-10-2026"));
    }

    [Fact]
    public void DailyTemplateFillsDates()
    {
        using var temp = new TempDirectory();
        temp.File("_templates/Daily.md", "# {{date:yyyy MMM d}}\n\nTitle {{title}}\n");

        Assert.Equal("# 2026 Oct 24\n\nTitle 2026-10-24\n", DailyNotes.NewDailyText(temp.Path, Now, "yyyy-MM-dd", "HH:mm", "\n"));
        Assert.StartsWith("# ", DailyNotes.NewDailyText(temp.Combine("other"), Now, "yyyy-MM-dd", "HH:mm", "\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void TemplatesAcceptCustomDateFormats() =>
        Assert.Equal("Week of 24.10 and {{date:%}}", PageTemplates.Apply("Week of {{date:dd.MM}} and {{date:%}}", "t", Now, "yyyy-MM-dd", "HH:mm"));
}
