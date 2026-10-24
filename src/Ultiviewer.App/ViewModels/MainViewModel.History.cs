using System.IO;
using CommunityToolkit.Mvvm.Input;
using Ultiviewer.Core.Files;
using Ultiviewer.Core.History;
using Ultiviewer.Core.Settings;

namespace Ultiviewer.App.ViewModels;

/// <summary>
/// Version history: what gets recorded when (before the first save of a session, saves at most once per
/// interval, outside changes and conflicts, before a restore), and restoring a version into a tab.
/// </summary>
public partial class MainViewModel
{
    private VersionHistory? _history;

    /// <summary>The history store, or null when history is turned off in settings.</summary>
    public VersionHistory? History
    {
        get
        {
            if (!Settings.KeepHistory)
            {
                return null;
            }

            _history ??= new VersionHistory(AppPaths.HistoryDirectory);
            _history.Interval = TimeSpan.FromMinutes(Math.Max(1, Settings.HistoryIntervalMinutes));
            return _history;
        }
    }

    /// <summary>The history window should open for this tab.</summary>
    public event Action<DocumentTabViewModel>? HistoryRequested;

    [RelayCommand]
    private void ShowHistory(DocumentTabViewModel? tab)
    {
        tab ??= ActiveTab;
        if (tab is null)
        {
            return;
        }

        if (tab.Path is null)
        {
            StatusMessage = "History is kept for files on disk; save this one first.";
            return;
        }

        if (History is null)
        {
            StatusMessage = "Version history is turned off in Settings.";
            return;
        }

        HistoryRequested?.Invoke(tab);
    }

    /// <summary>Opens a page (for example from the tree) and shows its history.</summary>
    public void ShowHistoryFor(string path)
    {
        OpenDocument(path, preview: false);
        if (ActiveTab is { } tab && tab.IsAt(path))
        {
            ShowHistory(tab);
        }
    }

    /// <summary>
    /// Before a tab's first save in this session, keeps the file as it is on disk, so the state before
    /// today's edits can always be recovered.
    /// </summary>
    private void RecordHistoryBaseline(DocumentTabViewModel tab)
    {
        if (tab.HistoryBaselineRecorded || tab.Path is not { } path || History is not { } history)
        {
            return;
        }

        tab.HistoryBaselineRecorded = true;
        try
        {
            if (File.Exists(path))
            {
                history.Record(path, TextDocumentFile.Load(path, Settings.DefaultLineEnding).Text, VersionReason.BeforeEditing);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void RecordVersion(DocumentTabViewModel tab, string text, VersionReason reason)
    {
        if (tab.Path is { } path)
        {
            History?.Record(path, text, reason);
        }
    }

    /// <summary>Replaces the tab's text with an older version as one undoable edit, keeping the replaced text in history.</summary>
    public void RestoreVersion(DocumentTabViewModel tab, string text)
    {
        if (tab.Document.Text == text)
        {
            return;
        }

        RecordVersion(tab, tab.Document.Text, VersionReason.BeforeRestore);
        tab.Document.Replace(0, tab.Document.TextLength, text);
        StatusMessage = $"Restored an earlier version of {tab.Title} (Ctrl+Z undoes it)";
    }
}
