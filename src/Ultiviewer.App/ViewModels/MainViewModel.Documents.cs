using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ultiviewer.App.Services;
using Ultiviewer.Core.Files;
using Ultiviewer.Core.History;
using Ultiviewer.Core.Notebooks;
using Ultiviewer.Core.Settings;
using Ultiviewer.Core.Text;

namespace Ultiviewer.App.ViewModels;

/// <summary>
/// Open documents: tabs, loading, saving (autosave for notebook pages, explicit for other files), unsaved-buffer
/// backups, reacting to changes made on disk, and session restore.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly BackupStore _backups = new(AppPaths.BackupDirectory);
    private readonly Stack<string> _closedTabs = new();
    private readonly Dictionary<DocumentTabViewModel, DispatcherTimer> _autosaveTimers = [];
    private DispatcherTimer? _backupTimer;
    private DispatcherTimer? _wordCountTimer;
    private bool _syncingSelection;
    private bool _checkingDisk;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private DocumentTabViewModel? _activeTab;

    [ObservableProperty]
    private int _caretLine = 1;

    [ObservableProperty]
    private int _caretColumn = 1;

    [ObservableProperty]
    private int _selectionLength;

    [ObservableProperty]
    private int _wordCount;

    public bool HasTabs => Primary.Tabs.Count > 0 || Secondary.Tabs.Count > 0;

    public string WindowTitle => ActiveTab is { } tab
        ? $"{tab.Title}{(tab.IsDirty ? " •" : "")} — {Core.AppInfo.Name}"
        : Core.AppInfo.Name;

    public string SaveStatus => ActiveTab switch
    {
        null => "",
        { SaveError: not null } => "Save failed",
        { IsDeletedOnDisk: true } => "Deleted on disk",
        { IsDirty: true } tab when AutosaveEligible(tab) => "Saving…",
        { IsDirty: true } => "Unsaved",
        _ => "Saved",
    };

    /// <summary>Asks the view to move keyboard focus into the editor.</summary>
    public event Action? EditorFocusRequested;

    /// <summary>Asks the view to move the caret of the active tab to a line (1-based).</summary>
    public event Action<int>? GoToLineRequested;

    /// <summary>Raised before/after a tab's text is replaced from disk, so the view can keep the caret.</summary>
    public event Action<DocumentTabViewModel>? TabReloading;

    public event Action<DocumentTabViewModel>? TabReloaded;

    /// <summary>Asks the view to store the active editor's caret/scroll into its tab.</summary>
    public event Action? CaptureEditorState;

    private void InitializeDocuments(IEnumerable<string> startupFiles)
    {
        RestoreTabs();
        foreach (var file in startupFiles)
        {
            OpenDocument(file, preview: false);
        }

        _backupTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => WriteBackups(), _dispatcher);
        _backupTimer.Start();
        _wordCountTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) =>
        {
            _wordCountTimer!.Stop();
            UpdateWordCount();
        }, _dispatcher);
        _wordCountTimer.Stop();

        // The tree realizes the restored selection asynchronously; that must not open a preview tab over the
        // restored or command-line documents.
        _syncingSelection = true;
        _dispatcher.BeginInvoke(() => _syncingSelection = false, DispatcherPriority.ApplicationIdle);
    }

    partial void OnSelectedNodeChanged(TreeNodeViewModel? value)
    {
        if (!_syncingSelection && value is { Kind: NodeKind.Page or NodeKind.File, IsEditing: false })
        {
            OpenDocument(value.Path, preview: true, focus: false);
        }
    }

    partial void OnActiveTabChanged(DocumentTabViewModel? oldValue, DocumentTabViewModel? newValue)
    {
        FollowActiveTab(newValue);
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnActiveTabPropertyChanged;
            if (IsOpen(oldValue) && oldValue.IsDirty && AutosaveEligible(oldValue))
            {
                SaveTab(oldValue, interactive: false);
            }
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnActiveTabPropertyChanged;
            if (newValue.Path is not null && FindNode(newValue.Path) is { } node && !node.IsSelected)
            {
                _syncingSelection = true;
                try
                {
                    SelectPath(newValue.Path);
                }
                finally
                {
                    _syncingSelection = false;
                }
            }
        }

        UpdateWordCount();
        OnPropertyChanged(nameof(SaveStatus));
        OnPropertyChanged(nameof(ActiveViewMode));
    }

    private void OnActiveTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentTabViewModel.IsDirty) or nameof(DocumentTabViewModel.Title))
        {
            OnPropertyChanged(nameof(WindowTitle));
        }

        if (e.PropertyName is nameof(DocumentTabViewModel.ViewMode) or nameof(DocumentTabViewModel.IsMarkdown) or nameof(DocumentTabViewModel.Language))
        {
            OnPropertyChanged(nameof(ActiveViewMode));
        }

        OnPropertyChanged(nameof(SaveStatus));
    }

    // ---- Opening ----

    /// <summary>Opens a file in a tab (or activates its tab). Preview tabs are replaced by the next preview.</summary>
    public DocumentTabViewModel? OpenDocument(string path, bool preview = false, int? line = null, bool focus = true)
    {
        path = Path.GetFullPath(path);

        // Already open: in the focused editor, or else in the other one (use "Open in other editor" for a second view).
        if ((FocusedGroup.Tabs.FirstOrDefault(t => t.IsAt(path)) ?? OtherGroup(FocusedGroup).Tabs.FirstOrDefault(t => t.IsAt(path))) is { } existing)
        {
            if (!preview)
            {
                existing.IsPreviewTab = false;
            }

            Activate(existing, line, focus);
            return existing;
        }

        if (!File.Exists(path))
        {
            if (!preview)
            {
                _dialogs.ShowError($"\"{path}\" doesn't exist.");
            }

            return null;
        }

        try
        {
            switch (TextDocumentFile.Inspect(path))
            {
                case LoadProblem.Binary:
                    if (preview)
                    {
                        StatusMessage = $"\"{Path.GetFileName(path)}\" isn't a text file. Press Enter to open it with its default app.";
                    }
                    else
                    {
                        ShellService.Open(path);
                    }

                    return null;
                case LoadProblem.TooLarge:
                    _dialogs.ShowError($"\"{Path.GetFileName(path)}\" is larger than {TextDocumentFile.MaxSize / (1024 * 1024)} MB and can't be opened.");
                    return null;
            }

            if (new FileInfo(path).Length > TextDocumentFile.LargeSize
                && !_dialogs.Confirm($"\"{Path.GetFileName(path)}\" is a large file. Open it anyway?"))
            {
                return null;
            }

            var loaded = TextDocumentFile.Load(path, Settings.DefaultLineEnding);
            var tab = CreateTab(path, loaded);
            tab.IsPreviewTab = preview;
            tab.PendingLine = line;
            AddTab(tab, replacePreview: preview);
            Activate(tab, null, focus);
            RememberRecent(path);
            return tab;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError($"Couldn't open \"{Path.GetFileName(path)}\":\n{ex.Message}");
            return null;
        }
    }

    partial void RememberRecent(string path);

    private DocumentTabViewModel CreateTab(string path, LoadedText loaded)
    {
        var tab = new DocumentTabViewModel(path, loaded.Text, loaded.Format)
        {
            KnownStamp = loaded.Stamp,
            KnownHash = loaded.Hash,
        };
        UpdateNotebookPageFlag(tab);
        tab.ViewMode = tab.IsMarkdown ? Settings.DefaultMarkdownViewMode : ViewMode.Edit;
        return tab;
    }

    private void UpdateNotebookPageFlag(DocumentTabViewModel tab) =>
        tab.IsNotebookPage = tab.Path is not null && NotebookScanner.IsPageFile(tab.Path) && NotebookOf(tab.Path) is not null;

    /// <summary>Adds a new document to the focused editor, replacing its preview tab when asked.</summary>
    private void AddTab(DocumentTabViewModel tab, bool replacePreview)
    {
        var group = FocusedGroup;
        var tabs = group.Tabs;
        var index = group.ActiveTab is null ? tabs.Count : tabs.IndexOf(group.ActiveTab) + 1;
        if (replacePreview && tabs.FirstOrDefault(t => t.IsPreviewTab && !t.IsDirty && t != tab) is { } old)
        {
            index = tabs.IndexOf(old);
            tabs.RemoveAt(index);
            group.Forget(old);
            if (!IsOpen(old))
            {
                Detach(old);
            }
        }

        Attach(tab);
        group.Adopt(tab);
        tabs.Insert(Math.Clamp(index, 0, tabs.Count), tab);
    }

    private void Activate(DocumentTabViewModel tab, int? line, bool focus)
    {
        if (ActiveTab == tab)
        {
            if (line is { } target)
            {
                GoToLineRequested?.Invoke(target);
            }
        }
        else
        {
            tab.PendingLine = line ?? tab.PendingLine;
            ActiveTab = tab;
        }

        if (focus)
        {
            EditorFocusRequested?.Invoke();
        }
    }

    public void FocusEditor() => EditorFocusRequested?.Invoke();

    public void GoToLine(int line) => GoToLineRequested?.Invoke(line);

    private void Attach(DocumentTabViewModel tab)
    {
        tab.Document.TextChanged += (_, _) => OnTabTextChanged(tab);
        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DocumentTabViewModel.IsDirty) && tab.IsDirty)
            {
                tab.IsPreviewTab = false;
            }
        };
    }

    private void Detach(DocumentTabViewModel tab)
    {
        if (_autosaveTimers.Remove(tab, out var timer))
        {
            timer.Stop();
        }
    }

    private void OnTabTextChanged(DocumentTabViewModel tab)
    {
        tab.IsPreviewTab = false;
        if (AutosaveEligible(tab))
        {
            if (!_autosaveTimers.TryGetValue(tab, out var timer))
            {
                timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher);
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    if (tab.IsDirty && IsOpen(tab))
                    {
                        SaveTab(tab, interactive: false);
                    }
                };
                _autosaveTimers[tab] = timer;
            }

            timer.Interval = TimeSpan.FromMilliseconds(Math.Max(200, Settings.AutosaveDelayMs));
            timer.Stop();
            timer.Start();
        }

        if (tab == ActiveTab)
        {
            _wordCountTimer?.Stop();
            _wordCountTimer?.Start();
            OnPropertyChanged(nameof(SaveStatus));
        }

        OnDocumentEdited(tab);
    }

    /// <summary>Called after every edit (preview refresh, etc.).</summary>
    partial void OnDocumentEdited(DocumentTabViewModel tab);

    /// <summary>Called after a document was written to disk (search index, etc.).</summary>
    partial void OnDocumentSaved(DocumentTabViewModel tab);

    private void UpdateWordCount() =>
        WordCount = ActiveTab is { } tab ? TextStatistics.CountWords(tab.Document.Text) : 0;

    public bool AutosaveEligible(DocumentTabViewModel tab) =>
        !tab.IsUntitled && (tab.IsNotebookPage ? Settings.AutosaveNotebookPages : Settings.AutosaveExternalFiles);

    // ---- Saving ----

    /// <summary>Saves a tab. Untitled tabs ask for a file name. Returns false when nothing was saved.</summary>
    public bool SaveTab(DocumentTabViewModel tab, bool interactive)
    {
        if (tab.Path is null)
        {
            return interactive && SaveTabAs(tab);
        }

        if (!interactive && ChangedOnDiskSinceLoad(tab))
        {
            // Never let autosave overwrite someone else's edit; ask first.
            CheckDiskState(tab);
            return false;
        }

        try
        {
            RecordHistoryBaseline(tab);
            Directory.CreateDirectory(Path.GetDirectoryName(tab.Path)!);
            var (stamp, hash) = TextDocumentFile.Save(tab.Path, tab.Document.Text, tab.Format);
            tab.MarkSaved(stamp, hash);
            RecordVersion(tab, tab.Document.Text, VersionReason.Saved);
            DropBackup(tab);
            if (interactive)
            {
                StatusMessage = $"Saved {tab.Title}";
            }

            OnPropertyChanged(nameof(SaveStatus));
            OnDocumentSaved(tab);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            tab.SaveError = ex.Message;
            OnPropertyChanged(nameof(SaveStatus));
            if (interactive)
            {
                _dialogs.ShowError($"Couldn't save \"{tab.Title}\":\n{ex.Message}");
            }
            else
            {
                StatusMessage = $"Couldn't save {tab.Title}: {ex.Message}";
            }

            return false;
        }
    }

    public bool SaveTabAs(DocumentTabViewModel tab)
    {
        var suggested = tab.Path is null ? tab.UntitledName + (tab.IsMarkdown ? ".md" : ".txt") : Path.GetFileName(tab.Path);
        var target = _dialogs.PickSaveFile("Save as", suggested, "Markdown|*.md|Text|*.txt|All files|*.*");
        if (target is null)
        {
            return false;
        }

        var oldPath = tab.Path;
        tab.Path = target;
        tab.Language = Editor.HighlightingRegistry.Instance.LanguageForFile(target);
        UpdateNotebookPageFlag(tab);
        tab.KnownStamp = null;
        tab.KnownHash = null;
        tab.HistoryBaselineRecorded = false; // a file being overwritten keeps its old text in history
        if (!SaveTab(tab, interactive: true))
        {
            tab.Path = oldPath;
            return false;
        }

        if (NotebookOf(target) is { } root)
        {
            RefreshNotebook(root);
        }

        return true;
    }

    [RelayCommand]
    private void Save(DocumentTabViewModel? tab)
    {
        if ((tab ?? ActiveTab) is { } target)
        {
            SaveTab(target, interactive: true);
        }
    }

    [RelayCommand]
    private void SaveAs(DocumentTabViewModel? tab)
    {
        if ((tab ?? ActiveTab) is { } target)
        {
            SaveTabAs(target);
        }
    }

    [RelayCommand]
    private void SaveAll()
    {
        foreach (var tab in OpenTabs.Where(t => t.IsDirty).ToList())
        {
            SaveTab(tab, interactive: true);
        }
    }

    // ---- Tab commands ----

    [RelayCommand]
    private void NewFile()
    {
        var used = OpenTabs.Where(t => t.IsUntitled).Select(t => t.UntitledName).ToHashSet();
        var n = 1;
        while (used.Contains($"new {n}"))
        {
            n++;
        }

        var tab = new DocumentTabViewModel(null, "", NewFileFormat(), $"new {n}")
        {
            ViewMode = Settings.DefaultMarkdownViewMode,
        };
        AddTab(tab, replacePreview: false);
        Activate(tab, null, focus: true);
    }

    private TextFileFormat NewFileFormat() =>
        new(TextFileFormat.Utf8, Settings.DefaultUtf8Bom, Settings.DefaultLineEnding);

    [RelayCommand]
    private void OpenFile()
    {
        if (_dialogs.PickFile("Open file", "All files|*.*|Markdown|*.md;*.markdown|Text|*.txt") is { } path)
        {
            OpenDocument(path, preview: false);
        }
    }

    [RelayCommand]
    private void Close(DocumentTabViewModel? tab)
    {
        if ((tab ?? ActiveTab) is { } target)
        {
            CloseTab(target);
        }
    }

    /// <summary>Closes a tab, asking to save unsaved text that isn't autosaved. False if the user cancelled.</summary>
    public bool CloseTab(DocumentTabViewModel tab) => CloseTab(tab, GroupOf(tab));

    /// <summary>
    /// Closes a tab in one group. While the other group still shows the document it just leaves this group;
    /// otherwise the document closes (saving or asking first). False if the user cancelled.
    /// </summary>
    public bool CloseTab(DocumentTabViewModel tab, EditorGroup? group)
    {
        group ??= GroupOf(tab);
        if (group is null)
        {
            return true;
        }

        if (tab == ActiveTab)
        {
            CaptureEditorState?.Invoke();
        }

        if (OtherGroup(group).Tabs.Contains(tab))
        {
            RemoveFromGroup(group, tab);
            return true;
        }

        if (tab.IsDirty)
        {
            if (AutosaveEligible(tab) && !tab.IsDeletedOnDisk)
            {
                if (!SaveTab(tab, interactive: true))
                {
                    return false;
                }
            }
            else
            {
                var answer = _dialogs.AskYesNoCancel($"Save changes to \"{tab.Title}\"?");
                if (answer is null || (answer == true && !SaveTab(tab, interactive: true)))
                {
                    return false;
                }
            }
        }

        DropBackup(tab);
        Detach(tab);
        if (tab.Path is not null)
        {
            _closedTabs.Push(tab.Path);
        }

        RemoveFromGroup(group, tab);
        return true;
    }

    [RelayCommand]
    private void CloseOthers(DocumentTabViewModel? tab)
    {
        var keep = tab ?? ActiveTab;
        var group = keep is null ? FocusedGroup : GroupOf(keep) ?? FocusedGroup;
        foreach (var other in group.Tabs.Where(t => t != keep && !t.IsPinned).ToList())
        {
            if (!CloseTab(other, group))
            {
                break;
            }
        }
    }

    /// <summary>Closes every unpinned tab in both editors.</summary>
    [RelayCommand]
    private void CloseAll()
    {
        foreach (var group in new[] { Secondary, Primary })
        {
            foreach (var tab in group.Tabs.Where(t => !t.IsPinned).ToList())
            {
                if (!CloseTab(tab, group))
                {
                    return;
                }
            }
        }
    }

    [RelayCommand]
    private void CloseToRight(DocumentTabViewModel? tab)
    {
        if ((tab ?? ActiveTab) is not { } anchor || GroupOf(anchor) is not { } group)
        {
            return;
        }

        foreach (var other in group.Tabs.Skip(group.Tabs.IndexOf(anchor) + 1).Where(t => !t.IsPinned).ToList())
        {
            if (!CloseTab(other, group))
            {
                break;
            }
        }
    }

    [RelayCommand]
    private void ReopenClosed()
    {
        while (_closedTabs.TryPop(out var path))
        {
            if (File.Exists(path))
            {
                OpenDocument(path, preview: false);
                return;
            }
        }
    }

    [RelayCommand]
    private void NextTab() => CycleTab(+1);

    [RelayCommand]
    private void PreviousTab() => CycleTab(-1);

    private void CycleTab(int step)
    {
        var tabs = FocusedGroup.Tabs;
        if (tabs.Count > 1 && ActiveTab is { } tab)
        {
            ActiveTab = tabs[(tabs.IndexOf(tab) + step + tabs.Count) % tabs.Count];
            EditorFocusRequested?.Invoke();
        }
    }

    [RelayCommand]
    private void TogglePin(DocumentTabViewModel? tab)
    {
        if ((tab ?? ActiveTab) is not { } target)
        {
            return;
        }

        target.IsPinned = !target.IsPinned;
        target.IsPreviewTab = false;
        foreach (var group in new[] { Primary, Secondary }.Where(g => g.Tabs.Contains(target)))
        {
            var tabs = group.Tabs;
            var pinnedCount = tabs.Count(t => t.IsPinned && t != target);
            var from = tabs.IndexOf(target);
            if (from != pinnedCount)
            {
                tabs.Move(from, Math.Min(pinnedCount, tabs.Count - 1));
            }
        }
    }

    [RelayCommand]
    private void KeepTabOpen(DocumentTabViewModel? tab)
    {
        if ((tab ?? ActiveTab) is { } target)
        {
            target.IsPreviewTab = false;
        }
    }

    [RelayCommand]
    private void Reload(DocumentTabViewModel? tab)
    {
        if ((tab ?? ActiveTab) is not { Path: { } path } target || !File.Exists(path))
        {
            return;
        }

        if (target.IsDirty && !_dialogs.Confirm($"Reload \"{target.Title}\" from disk and lose your unsaved changes?", warning: true))
        {
            return;
        }

        ReloadTab(target, TextDocumentFile.Load(path, Settings.DefaultLineEnding));
    }

    [RelayCommand]
    private void RevealTabInTree(DocumentTabViewModel? tab)
    {
        if ((tab ?? ActiveTab) is { Path: { } path })
        {
            if (SelectPath(path) is null)
            {
                ShellService.RevealInExplorer(path);
            }
        }
    }

    [RelayCommand]
    private void CopyTabPath(DocumentTabViewModel? tab)
    {
        if ((tab ?? ActiveTab) is { Path: { } path })
        {
            Clipboard.SetText(path);
            StatusMessage = "Path copied";
        }
    }

    [RelayCommand]
    private void OpenTabFolder(DocumentTabViewModel? tab)
    {
        if ((tab ?? ActiveTab) is { Path: { } path })
        {
            ShellService.RevealInExplorer(path);
        }
    }

    /// <summary>Reorders a tab within its group, or moves it there from the other group (drag and drop).</summary>
    public void MoveTab(EditorGroup group, DocumentTabViewModel tab, int newIndex)
    {
        if (!group.Tabs.Contains(tab) && OtherGroup(group).Tabs.Contains(tab))
        {
            group.Adopt(tab);
            group.Tabs.Insert(Math.Clamp(newIndex, 0, group.Tabs.Count), tab);
            RemoveFromGroup(OtherGroup(group), tab);
            return;
        }

        var from = group.Tabs.IndexOf(tab);
        if (from >= 0 && newIndex >= 0 && newIndex < group.Tabs.Count && from != newIndex)
        {
            group.Tabs.Move(from, newIndex);
        }
    }

    // ---- Format changes (status bar) ----

    public void SetLineEnding(DocumentTabViewModel tab, LineEnding lineEnding)
    {
        var text = tab.Document.Text;
        var normalized = TextFileFormat.NormalizeLineEndings(text, lineEnding);
        tab.Format = tab.Format with { LineEnding = lineEnding };
        if (normalized != text)
        {
            tab.Document.Text = normalized;
        }
        else
        {
            tab.IsDirty = true;
        }
    }

    public void SetEncoding(DocumentTabViewModel tab, Encoding encoding, bool bom)
    {
        tab.Format = tab.Format with { Encoding = encoding, HasBom = bom };
        tab.IsDirty = true;
        OnPropertyChanged(nameof(SaveStatus));
        if (AutosaveEligible(tab))
        {
            SaveTab(tab, interactive: false);
        }
    }

    /// <summary>Re-reads the file assuming a different encoding (the file isn't changed).</summary>
    public void ReopenWithEncoding(DocumentTabViewModel tab, Encoding encoding)
    {
        if (tab.Path is not { } path || (tab.IsDirty && !_dialogs.Confirm("Discard unsaved changes and reopen with a different encoding?", warning: true)))
        {
            return;
        }

        var bytes = TextDocumentFile.ReadAllBytesShared(path);
        var (_, bomLength) = TextFileFormat.DetectEncoding(bytes);
        var text = encoding.GetString(bytes, bomLength, bytes.Length - bomLength);
        var format = new TextFileFormat(encoding, bomLength > 0, TextFileFormat.DetectLineEnding(text) ?? tab.Format.LineEnding);
        ReloadTab(tab, new LoadedText(text, format, FileStamp.Read(path) ?? default, TextDocumentFile.Hash(bytes)));
    }

    public void SetLanguage(DocumentTabViewModel tab, string language) => tab.Language = language;

    // ---- Changes on disk ----

    partial void OnFileChangedOnDisk(string path)
    {
        foreach (var tab in OpenTabs.Where(t => t.IsAt(path)).ToList())
        {
            CheckDiskState(tab);
        }

        OnNotebookFileChanged(path);
    }

    /// <summary>Called for every file change inside a notebook (search index, etc.).</summary>
    partial void OnNotebookFileChanged(string path);

    /// <summary>Re-checks every open file; called when the window is activated (like Notepad++).</summary>
    public void CheckAllDiskStates()
    {
        foreach (var tab in OpenTabs)
        {
            CheckDiskState(tab);
        }
    }

    private bool ChangedOnDiskSinceLoad(DocumentTabViewModel tab)
    {
        if (tab.Path is null || tab.KnownStamp is null)
        {
            return false;
        }

        var stamp = FileStamp.Read(tab.Path);
        if (stamp is null || stamp == tab.KnownStamp)
        {
            return false;
        }

        try
        {
            return TextDocumentFile.HashFile(tab.Path) != tab.KnownHash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void CheckDiskState(DocumentTabViewModel tab)
    {
        if (tab.Path is null || _checkingDisk)
        {
            return;
        }

        var stamp = FileStamp.Read(tab.Path);
        if (stamp is null)
        {
            if (!tab.IsDeletedOnDisk && !File.Exists(tab.Path))
            {
                tab.IsDeletedOnDisk = true;
                StatusMessage = $"\"{tab.Title}\" was deleted or moved outside {Core.AppInfo.Name}. Save it to recreate the file.";
                OnPropertyChanged(nameof(SaveStatus));
            }

            return;
        }

        tab.IsDeletedOnDisk = false;
        if (stamp == tab.KnownStamp)
        {
            return;
        }

        LoadedText loaded;
        try
        {
            loaded = TextDocumentFile.Load(tab.Path, Settings.DefaultLineEnding);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // Probably still being written; the next change event retries.
        }

        if (loaded.Hash == tab.KnownHash)
        {
            tab.KnownStamp = loaded.Stamp;
            return;
        }

        if (!tab.IsDirty)
        {
            RecordVersion(tab, tab.Document.Text, VersionReason.BeforeOutsideChange);
            RecordVersion(tab, loaded.Text, VersionReason.ChangedOnDisk);
            ReloadTab(tab, loaded);
            StatusMessage = $"Reloaded \"{tab.Title}\" — it was changed by another program";
            return;
        }

        // A conflict: both versions go to history whatever the user picks, so neither can be lost.
        RecordVersion(tab, tab.Document.Text, VersionReason.Conflict);
        RecordVersion(tab, loaded.Text, VersionReason.ChangedOnDisk);
        _checkingDisk = true;
        try
        {
            if (_dialogs.Confirm(
                    $"\"{tab.Title}\" was changed by another program.\n\nReload it and replace the unsaved changes made here?\n\nCancel keeps your version; saving it will overwrite the other change. Both versions are kept in File → Page history.",
                    warning: true))
            {
                ReloadTab(tab, loaded);
            }
            else
            {
                tab.KnownStamp = loaded.Stamp;
                tab.KnownHash = loaded.Hash;
            }
        }
        finally
        {
            _checkingDisk = false;
        }
    }

    private void ReloadTab(DocumentTabViewModel tab, LoadedText loaded)
    {
        if (tab == ActiveTab)
        {
            TabReloading?.Invoke(tab);
        }

        tab.Reload(loaded);
        DropBackup(tab);
        if (tab == ActiveTab)
        {
            TabReloaded?.Invoke(tab);
        }

        OnPropertyChanged(nameof(SaveStatus));
    }

    partial void OnItemMoved(string oldPath, string newPath)
    {
        _history?.Move(oldPath, newPath);
        foreach (var tab in OpenTabs)
        {
            if (tab.Path is not { } path)
            {
                continue;
            }

            if (tab.IsAt(oldPath))
            {
                tab.Path = newPath;
            }
            else if (FileSystemMoves.IsSameOrInside(path, oldPath))
            {
                tab.Path = Path.Combine(newPath, Path.GetRelativePath(oldPath, path));
            }
            else
            {
                continue;
            }

            tab.KnownStamp = FileStamp.Read(tab.Path);
            UpdateNotebookPageFlag(tab);
        }

        OnPropertyChanged(nameof(WindowTitle));
    }

    partial void OnItemDeleted(string path)
    {
        foreach (var tab in OpenTabs.Where(t => t.IsAt(path)).ToList())
        {
            if (tab.IsDirty)
            {
                tab.IsDeletedOnDisk = true;
            }
            else
            {
                DropBackup(tab);
                Detach(tab);
                RemoveFromGroup(Secondary, tab);
                RemoveFromGroup(Primary, tab);
            }
        }
    }

    partial void OnNewPageReady(string path) => OpenDocument(path, preview: false, focus: true);

    // ---- Backups and session ----

    private void WriteBackups(bool force = false)
    {
        foreach (var tab in OpenTabs)
        {
            var autosaved = AutosaveEligible(tab) && !tab.IsDeletedOnDisk;
            if (!tab.IsDirty || (autosaved && !force) || tab.Version == tab.BackupVersion)
            {
                continue;
            }

            tab.BackupName ??= BackupStore.NewName();
            try
            {
                _backups.Write(tab.BackupName, tab.Document.Text);
                tab.BackupVersion = tab.Version;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ErrorLog.Write(ex, "Backup");
            }
        }
    }

    private void DropBackup(DocumentTabViewModel tab)
    {
        if (tab.BackupName is { } name)
        {
            _backups.Delete(name);
            tab.BackupName = null;
            tab.BackupVersion = -1;
        }
    }

    /// <summary>
    /// Called when the window closes: autosaves what is autosaved and snapshots everything else, so the next
    /// start restores unsaved buffers instead of asking about them (Notepad++ behaviour).
    /// </summary>
    private void CaptureDocumentState()
    {
        CaptureEditorState?.Invoke();
        foreach (var tab in OpenTabs.Where(t => t.IsDirty && AutosaveEligible(t) && !t.IsDeletedOnDisk).ToList())
        {
            SaveTab(tab, interactive: false);
        }

        WriteBackups(force: true);
        OpenTabState State(DocumentTabViewModel t, EditorGroup group) => new()
        {
            Path = t.Path ?? "",
            UntitledName = t.IsUntitled ? t.UntitledName : null,
            Language = t.IsUntitled ? t.Language : null,
            CaretOffset = t.CaretOffset,
            VerticalOffset = t.VerticalOffset,
            ViewMode = group.ModeOf(t),
            Pinned = t.IsPinned,
            BackupFile = t.IsDirty ? t.BackupName : null,
            Bookmarks = t.BookmarkLines().ToList(),
        };

        Session.Tabs = Primary.Tabs.Select(t => State(t, Primary)).ToList();
        Session.ActiveTab = Primary.ActiveTab is null ? -1 : Primary.Tabs.IndexOf(Primary.ActiveTab);
        Session.IsSplit = IsSplit;
        Session.SplitVertical = SplitVertical;
        Session.SplitTabs = IsSplit ? Secondary.Tabs.Select(t => State(t, Secondary)).ToList() : [];
        Session.SplitActiveTab = Secondary.ActiveTab is null ? -1 : Secondary.Tabs.IndexOf(Secondary.ActiveTab);
        Session.FocusedGroup = FocusedGroup.Index;
        _backups.DeleteAllExcept(Session.Tabs.Concat(Session.SplitTabs).Select(t => t.BackupFile).OfType<string>());
    }

    private void RestoreTabs()
    {
        foreach (var state in Session.Tabs)
        {
            if (RestoreTab(state) is { } tab)
            {
                Attach(tab);
                Primary.Tabs.Add(tab);
            }
        }

        Primary.ActiveTab = Session.ActiveTab >= 0 && Session.ActiveTab < Primary.Tabs.Count ? Primary.Tabs[Session.ActiveTab] : Primary.Tabs.LastOrDefault();
        if (Session.IsSplit)
        {
            foreach (var state in Session.SplitTabs)
            {
                // A document open on both sides is one tab object shown twice.
                var shared = Primary.Tabs.FirstOrDefault(t => state.Path.Length > 0 ? t.IsAt(state.Path) : t.IsUntitled && t.UntitledName == state.UntitledName);
                var tab = shared ?? RestoreTab(state);
                if (tab is null || Secondary.Tabs.Contains(tab))
                {
                    continue;
                }

                if (shared is null)
                {
                    Attach(tab);
                }

                Secondary.Adopt(tab);
                Secondary.Tabs.Add(tab);
                Secondary.SetMode(tab, state.ViewMode ?? tab.ViewMode);
            }

            Secondary.ActiveTab = Session.SplitActiveTab >= 0 && Session.SplitActiveTab < Secondary.Tabs.Count ? Secondary.Tabs[Session.SplitActiveTab] : Secondary.Tabs.LastOrDefault();
            IsSplit = Secondary.Tabs.Count > 0;
            SplitVertical = Session.SplitVertical;
        }

        FocusedGroup = IsSplit && Session.FocusedGroup == 1 ? Secondary : Primary;
        ActiveTab = FocusedGroup.ActiveTab;
    }

    /// <summary>Recreates one tab from the session (with its unsaved text from the backup), or null.</summary>
    private DocumentTabViewModel? RestoreTab(OpenTabState state)
    {
        var backup = state.BackupFile is { } name ? _backups.Read(name) : null;
        DocumentTabViewModel? tab = null;
        try
        {
            if (string.IsNullOrEmpty(state.Path))
            {
                if (backup is not null)
                {
                    tab = new DocumentTabViewModel(null, "", NewFileFormat(), state.UntitledName ?? "new 1");
                    if (state.Language is { } language)
                    {
                        tab.Language = language;
                    }
                }
            }
            else if (File.Exists(state.Path) && TextDocumentFile.Inspect(state.Path) == LoadProblem.None)
            {
                tab = CreateTab(state.Path, TextDocumentFile.Load(state.Path, Settings.DefaultLineEnding));
            }
            else if (backup is not null)
            {
                tab = new DocumentTabViewModel(state.Path, "", NewFileFormat()) { IsDeletedOnDisk = true };
                UpdateNotebookPageFlag(tab);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(ex, $"Restoring tab {state.Path}");
        }

        if (tab is null)
        {
            return null;
        }

        if (backup is not null)
        {
            tab.Document.Text = backup; // an undoable edit, so the tab shows as unsaved
            tab.BackupName = state.BackupFile;
            tab.BackupVersion = tab.Version;
        }

        tab.CaretOffset = Math.Min(state.CaretOffset, tab.Document.TextLength);
        tab.SelectionStart = tab.CaretOffset;
        tab.VerticalOffset = state.VerticalOffset;
        tab.ViewMode = state.ViewMode ?? tab.ViewMode;
        tab.IsPinned = state.Pinned;
        tab.SetBookmarks(state.Bookmarks);
        return tab;
    }
}
