using System.IO;
using CommunityToolkit.Mvvm.Input;
using Ultiviewer.Core.Capture;
using Ultiviewer.Core.Files;

namespace Ultiviewer.App.ViewModels;

/// <summary>
/// Daily notes and quick capture: today's page in the capture notebook, and appending short notes to it (or to
/// the notebook's Inbox) from the quick-note window, which the global hotkey opens from anywhere in Windows.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Set when the app should really exit (File → Exit, the tray's Exit, Windows shutting down) rather than hide in the tray.</summary>
    public bool ExitRequested { get; set; }

    /// <summary>The quick-note window should open.</summary>
    public event Action? QuickCaptureRequested;

    /// <summary>Hotkey or tray settings changed.</summary>
    public event Action? CaptureSettingsChanged;

    /// <summary>The notebook for daily notes and the Inbox: the one chosen in settings, else the first one.</summary>
    public TreeNodeViewModel? CaptureNotebook =>
        Notebooks.FirstOrDefault(n => Settings.DailyNotesNotebook is { Length: > 0 } chosen && string.Equals(Path.GetFullPath(n.Path), Path.GetFullPath(chosen), StringComparison.OrdinalIgnoreCase))
        ?? Notebooks.FirstOrDefault();

    [RelayCommand]
    private void OpenToday() => OpenDailyNote(DateTime.Today);

    [RelayCommand]
    private void ShowQuickNote() => QuickCaptureRequested?.Invoke();

    public void OpenDailyNote(DateTime date)
    {
        if (EnsureCapturePage(CaptureTarget.DailyNote, date) is { } path)
        {
            OpenDocument(path, preview: false, focus: true);
        }
    }

    /// <summary>Opens today's note or the Inbox, creating it when missing.</summary>
    public void OpenCapturePage(CaptureTarget target)
    {
        if (EnsureCapturePage(target, DateTime.Today) is { } path)
        {
            OpenDocument(path, preview: false, focus: true);
        }
    }

    /// <summary>The page a capture target writes to (it may not exist yet), or null without a notebook.</summary>
    public string? CapturePath(CaptureTarget target, DateTime date) => CaptureNotebook is not { } notebook
        ? null
        : target == CaptureTarget.Inbox
            ? DailyNotes.InboxPath(notebook.Path)
            : DailyNotes.PathFor(notebook.Path, Settings.DailyNotesFolder, Settings.DailyNoteFormat, date);

    /// <summary>
    /// Appends <paramref name="note"/> to the target page, creating the page (from the Daily template) if needed.
    /// An open tab gets the text as an undoable edit and saves; otherwise the file is written directly.
    /// Returns the page path, or null when there is no notebook (the status bar says so).
    /// </summary>
    public string? Capture(string note, CaptureTarget target)
    {
        if (EnsureCapturePage(target, DateTime.Today) is not { } path)
        {
            return null;
        }

        var now = DateTime.Now;
        try
        {
            if (Tabs.FirstOrDefault(t => t.IsAt(path)) is { } tab)
            {
                var document = tab.Document;
                var updated = QuickCapture.Append(document.Text, note, now, Settings.TimeFormat, tab.Format.NewLine);
                var keep = document.Text.TrimEnd('\r', '\n', ' ', '\t').Length;
                document.Replace(keep, document.TextLength - keep, updated[keep..]);
                SaveTab(tab, interactive: false);
            }
            else
            {
                var loaded = TextDocumentFile.Load(path, Settings.DefaultLineEnding);
                var updated = QuickCapture.Append(loaded.Text, note, now, Settings.TimeFormat, loaded.Format.NewLine);
                History?.Record(path, loaded.Text, Core.History.VersionReason.BeforeEditing);
                TextDocumentFile.Save(path, updated, loaded.Format);
                History?.Record(path, updated, Core.History.VersionReason.Saved);
            }

            StatusMessage = $"Added a note to {Path.GetFileNameWithoutExtension(path)}";
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError($"Couldn't add the note to {path}:\n{ex.Message}");
            return null;
        }
    }

    /// <summary>Creates the target page if it doesn't exist and returns its path.</summary>
    private string? EnsureCapturePage(CaptureTarget target, DateTime date)
    {
        if (CaptureNotebook is not { } notebook || CapturePath(target, date) is not { } path)
        {
            StatusMessage = "Create or open a notebook first: daily notes and the Inbox live in a notebook.";
            return null;
        }

        if (File.Exists(path))
        {
            return path;
        }

        var format = TextFileFormat.Utf8Default(Settings.DefaultLineEnding);
        var text = target == CaptureTarget.Inbox
            ? "# " + DailyNotes.InboxName + format.NewLine + format.NewLine
            : DailyNotes.NewDailyText(notebook.Path, date, Settings.DateFormat, Settings.TimeFormat, format.NewLine);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            TextDocumentFile.Save(path, TextFileFormat.NormalizeLineEndings(text, format.LineEnding), format);
            RefreshNotebook(notebook);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError($"Couldn't create {path}:\n{ex.Message}");
            return null;
        }
    }
}
