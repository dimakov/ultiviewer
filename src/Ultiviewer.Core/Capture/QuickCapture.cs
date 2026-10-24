using Ultiviewer.Core.Notebooks;

namespace Ultiviewer.Core.Capture;

/// <summary>Where quick notes go.</summary>
public enum CaptureTarget
{
    /// <summary>Today's daily note (created from the "Daily" template when missing).</summary>
    DailyNote,

    /// <summary>The notebook's Inbox page.</summary>
    Inbox,
}

/// <summary>Daily notes: one page per day in a section of a notebook, named by a date format.</summary>
public static class DailyNotes
{
    public const string TemplateName = "Daily";

    public const string InboxName = "Inbox";

    /// <summary>The page for <paramref name="date"/>: <c>&lt;notebook&gt;\&lt;folder&gt;\&lt;date&gt;.md</c>.</summary>
    public static string PathFor(string notebookRoot, string folder, string nameFormat, DateTime date)
    {
        string name;
        try
        {
            name = date.ToString(string.IsNullOrWhiteSpace(nameFormat) ? "yyyy-MM-dd" : nameFormat, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            name = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        }

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '-');
        }

        var section = string.IsNullOrWhiteSpace(folder) ? notebookRoot : Path.Combine(notebookRoot, folder.Trim());
        return Path.Combine(section, name + NotebookScanner.PageExtension);
    }

    public static string InboxPath(string notebookRoot) => Path.Combine(notebookRoot, InboxName + NotebookScanner.PageExtension);

    /// <summary>A new daily note: the notebook's <c>_templates/Daily.md</c> when there is one, else a dated heading.</summary>
    public static string NewDailyText(string notebookRoot, DateTime date, string dateFormat, string timeFormat, string newLine)
    {
        var template = Path.Combine(notebookRoot, PageTemplates.FolderName, TemplateName + NotebookScanner.PageExtension);
        if (File.Exists(template))
        {
            try
            {
                return PageTemplates.Apply(File.ReadAllText(template), date.ToString(dateFormat, System.Globalization.CultureInfo.CurrentCulture), date, dateFormat, timeFormat);
            }
            catch (IOException)
            {
            }
        }

        return "# " + date.ToString("dddd, d MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture) + newLine + newLine;
    }
}

/// <summary>Appending quick notes to a page.</summary>
public static class QuickCapture
{
    /// <summary>
    /// <paramref name="existing"/> with <paramref name="note"/> added at the end, stamped with the time: a one-line
    /// note becomes a list item (<c>- 14:05 Call Dana</c>) that joins a list of earlier captures; a longer note
    /// becomes a bold time followed by its text. Separated from other content by a blank line.
    /// </summary>
    public static string Append(string existing, string note, DateTime now, string timeFormat, string newLine)
    {
        var text = note.Trim().ReplaceLineEndings(newLine);
        if (text.Length == 0)
        {
            return existing;
        }

        var time = now.ToString(string.IsNullOrWhiteSpace(timeFormat) ? "HH:mm" : timeFormat, System.Globalization.CultureInfo.CurrentCulture);
        var singleLine = !text.Contains(newLine, StringComparison.Ordinal);
        var block = singleLine ? $"- {time} {text}" : $"**{time}**{newLine}{text}";

        var body = existing.TrimEnd('\r', '\n', ' ', '\t');
        if (body.Length == 0)
        {
            return block + newLine;
        }

        var lastLine = body[(body.LastIndexOfAny(['\r', '\n']) + 1)..];
        var continuesList = singleLine && lastLine.StartsWith("- ", StringComparison.Ordinal);
        return body + (continuesList ? newLine : newLine + newLine) + block + newLine;
    }
}
