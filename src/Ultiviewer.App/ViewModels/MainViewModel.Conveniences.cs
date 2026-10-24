using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Ultiviewer.App.Services;
using Ultiviewer.Core.Files;
using Ultiviewer.Core.Links;
using Ultiviewer.Core.Markdown;
using Ultiviewer.Core.Notebooks;
using Ultiviewer.Core.Search;

namespace Ultiviewer.App.ViewModels;

/// <summary>
/// One task in the To-do pane. Setting <see cref="Done"/> (mouse, keyboard or UI Automation) ticks the task in
/// its page.
/// </summary>
public sealed class TaskEntry(string path, string title, int line, bool done, string text, string location, Action<TaskEntry> toggle)
    : ObservableObject
{
    private bool _done = done;

    public string Path { get; } = path;

    public string Title { get; } = title;

    public int Line { get; } = line;

    public string Text { get; } = text;

    public string Location { get; } = location;

    /// <summary>Dates, priority, recurrence and reminder written in the task (Obsidian Tasks syntax).</summary>
    public TaskInfo Info { get; } = TaskInfo.Parse(text);

    /// <summary>The task without its metadata.</summary>
    public string Description => Info.Description.Length > 0 ? Info.Description : Text;

    /// <summary>The To-do pane's group: Overdue, Today, Upcoming (7 days), Later, No date, Done.</summary>
    public string Group => GroupAndRank(DateOnly.FromDateTime(DateTime.Today)).Group;

    public int GroupRank => GroupAndRank(DateOnly.FromDateTime(DateTime.Today)).Rank;

    public bool IsOverdue => GroupRank == 0;

    /// <summary>"Today", "Tomorrow", "Fri", "3 Nov", "2 days overdue"…, or null without a date.</summary>
    public string? DueLabel
    {
        get
        {
            if (Info.PlannedDate is not { } date)
            {
                return null;
            }

            var today = DateOnly.FromDateTime(DateTime.Today);
            var days = date.DayNumber - today.DayNumber;
            var label = days switch
            {
                0 => "Today",
                1 => "Tomorrow",
                -1 when !Done => "Yesterday · overdue",
                < 0 when !Done => $"{-days} days overdue",
                > 1 and < 7 => date.ToString("dddd", System.Globalization.CultureInfo.CurrentCulture),
                _ => date.ToString(date.Year == today.Year ? "d MMM" : "d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture),
            };
            return Info.Due is null && Info.Scheduled is not null ? "Scheduled " + label.ToLowerInvariant() : label;
        }
    }

    public string? PriorityGlyph => Info.Priority switch
    {
        TaskPriority.Highest => "🔺",
        TaskPriority.High => "⏫",
        TaskPriority.Medium => "🔼",
        TaskPriority.Low => "🔽",
        TaskPriority.Lowest => "⏬",
        _ => null,
    };

    public string? Extras => string.Join(" ", new[] { PriorityGlyph, Info.Recurrence is null ? null : "🔁", Info.Reminder is { } at ? "⏰ " + at.ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture) : null }.Where(s => s is not null)) is { Length: > 0 } extras ? extras : null;

    private (string Group, int Rank) GroupAndRank(DateOnly today)
    {
        if (Done)
        {
            return ("Done", 5);
        }

        if ((Info.Due ?? Info.Scheduled) is not { } date)
        {
            return ("No date", 4);
        }

        var days = date.DayNumber - today.DayNumber;
        return days switch
        {
            < 0 => ("Overdue", 0),
            0 => ("Today", 1),
            < 8 => ("Upcoming", 2),
            _ => ("Later", 3),
        };
    }

    public bool Done
    {
        get => _done;
        set
        {
            if (SetProperty(ref _done, value))
            {
                toggle(this);
            }
        }
    }

    public override string ToString() => $"{(Done ? "Done" : "To do")}: {Text} — {Title}";
}

/// <summary>OneNote conveniences: link upkeep after moves, templates, favorites, tags and to-do summaries.</summary>
public sealed partial class MainViewModel
{
    private DispatcherTimer? _summaryRefresh;

    public ObservableCollection<TagCount> AllTags { get; } = [];

    public ObservableCollection<TaskEntry> Tasks { get; } = [];

    [ObservableProperty]
    private bool _showDoneTasks;

    partial void OnShowDoneTasksChanged(bool value) => RefreshSummaries();

    /// <summary>The To-do list was reloaded (reminders check it).</summary>
    public event Action? TasksRefreshed;

    // ---- Links after renames and moves ----

    /// <summary>
    /// After <paramref name="before"/> moved to <paramref name="newPath"/>: the moved pages' own relative links are
    /// rebased automatically; links in other pages that point at it are updated after a confirmation.
    /// </summary>
    private async void UpdateLinksAfterMove(NotebookNode before, string newPath)
    {
        var moves = new List<PathMove> { new(before.Path, newPath) };
        string? oldTitle = null, newTitle = null;
        if (before.Kind == NodeKind.Page)
        {
            var folder = Path.GetDirectoryName(newPath)!;
            newTitle = Path.GetFileNameWithoutExtension(newPath);
            oldTitle = before.Name;
            moves.Add(new PathMove(before.AssetsFolder!, Path.Combine(folder, newTitle + NotebookScanner.AssetsSuffix)));
            moves.Add(new PathMove(before.ChildFolder!, Path.Combine(folder, newTitle)));
        }

        var openTexts = OpenTabs.Where(t => t.Path is not null).ToDictionary(t => t.Path!, t => t.Document.Text, StringComparer.OrdinalIgnoreCase);
        var roots = Notebooks.Select(n => n.Path).ToList();
        var plan = await Task.Run(() =>
        {
            var changes = new List<(string Path, int Count, bool Own)>();
            foreach (var page in roots.SelectMany(NotebookScanner.EnumeratePageFiles))
            {
                string text;
                try
                {
                    text = openTexts.TryGetValue(page, out var open) ? open : TextDocumentFile.Load(page).Text;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                var (_, count) = LinkUpdater.Rewrite(text, page, moves, oldTitle, newTitle);
                if (count > 0)
                {
                    var own = !string.Equals(LinkUpdater.Unmap(page, moves), page, StringComparison.OrdinalIgnoreCase);
                    changes.Add((page, count, own));
                }
            }

            return changes;
        });

        foreach (var (path, _, _) in plan.Where(p => p.Own))
        {
            ApplyLinkUpdate(path, moves, oldTitle, newTitle);
        }

        var inbound = plan.Where(p => !p.Own).ToList();
        if (inbound.Count == 0)
        {
            return;
        }

        var total = inbound.Sum(p => p.Count);
        var name = Path.GetFileNameWithoutExtension(newPath);
        var pages = string.Join("\n", inbound.Take(10).Select(p => "  " + Path.GetFileNameWithoutExtension(p.Path))) + (inbound.Count > 10 ? $"\n  … and {inbound.Count - 10} more" : "");
        if (_dialogs.Confirm($"{total} link{(total == 1 ? "" : "s")} in {inbound.Count} page{(inbound.Count == 1 ? "" : "s")} {(total == 1 ? "points" : "point")} to \"{name}\" at its old location:\n\n{pages}\n\nUpdate them?"))
        {
            foreach (var (path, _, _) in inbound)
            {
                ApplyLinkUpdate(path, moves, oldTitle, newTitle);
            }

            StatusMessage = $"Updated {total} link{(total == 1 ? "" : "s")} in {inbound.Count} page{(inbound.Count == 1 ? "" : "s")}";
        }
    }

    /// <summary>Rewrites one page: through its tab (undoable, then saved as usual) or directly on disk.</summary>
    private void ApplyLinkUpdate(string path, IReadOnlyList<PathMove> moves, string? oldTitle, string? newTitle)
    {
        if (OpenTabs.FirstOrDefault(t => t.IsAt(path)) is { } tab)
        {
            var (text, count) = LinkUpdater.Rewrite(tab.Document.Text, path, moves, oldTitle, newTitle);
            if (count > 0)
            {
                tab.Document.Replace(0, tab.Document.TextLength, text);
            }

            return;
        }

        try
        {
            var loaded = TextDocumentFile.Load(path);
            var (text, count) = LinkUpdater.Rewrite(loaded.Text, path, moves, oldTitle, newTitle);
            if (count > 0)
            {
                TextDocumentFile.Save(path, text, loaded.Format);
                _search?.Refresh(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(ex, $"Updating links in {path}");
        }
    }

    // ---- Templates ----

    public IReadOnlyList<string> TemplatesForCurrentNotebook() =>
        CurrentNotebook() is { } root ? PageTemplates.List(root.Path) : [];

    private TreeNodeViewModel? CurrentNotebook() =>
        (SelectedNode?.Path ?? ActiveTab?.Path) is { } path ? NotebookOf(path) : Notebooks.FirstOrDefault();

    public void NewPageFromTemplate(string templatePath)
    {
        if (TargetNode(null) is not { } target)
        {
            return;
        }

        var (container, after) = target.Kind is NodeKind.Notebook or NodeKind.Section
            ? (target.Path, (string?)null)
            : (target.Node.ParentFolder, target.Node.EntryName);
        if (Core.Files.FileSystemMoves.IsSameOrInside(container, Path.Combine(target.Root.Path, PageTemplates.FolderName)))
        {
            // Never create pages among the templates themselves.
            (container, after) = (target.Root.Path, null);
        }

        var suggested = NotebookOperations.UniqueName(container, Path.GetFileNameWithoutExtension(templatePath));
        var title = _dialogs.Prompt("New page from template", "Page title:", suggested, t =>
            FileNameRules.Validate(t) ?? (File.Exists(Path.Combine(container, t + NotebookScanner.PageExtension)) ? "A page with this title already exists here." : null));
        if (title is null)
        {
            return;
        }

        string? created = null;
        if (TryRun(() =>
            {
                var template = TextDocumentFile.Load(templatePath).Text;
                var content = PageTemplates.Apply(template, title, DateTime.Now, Settings.DateFormat, Settings.TimeFormat);
                created = _ops.CreatePage(container, title, content, after);
            }))
        {
            RefreshNotebook(target.Root);
            SelectPath(created!);
            OpenDocument(created!, preview: false, focus: true);
        }
    }

    /// <summary>Shows the notebook's template folder, creating it with an example the first time.</summary>
    public void OpenTemplatesFolder()
    {
        if (CurrentNotebook() is not { } root)
        {
            return;
        }

        var folder = Path.Combine(root.Path, PageTemplates.FolderName);
        if (!Directory.Exists(folder))
        {
            TryRun(() => _ops.CreatePage(Directory.CreateDirectory(folder).FullName, "Meeting",
                "# {{title}}\n\n{{datetime}}\n\n## Attendees\n\n- \n\n## Notes\n\n- \n\n## Action items\n\n- [ ] \n"));
            RefreshNotebook(root);
        }

        if (SelectPath(folder) is { } node)
        {
            node.IsExpanded = true;
        }
    }

    // ---- Favorites ----

    public bool IsFavorite(string path) =>
        Session.FavoritePages.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

    public void ToggleFavorite(string path)
    {
        if (IsFavorite(path))
        {
            Session.FavoritePages.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            StatusMessage = $"Removed \"{Path.GetFileNameWithoutExtension(path)}\" from favorites";
        }
        else
        {
            Session.FavoritePages.Add(path);
            StatusMessage = $"Added \"{Path.GetFileNameWithoutExtension(path)}\" to favorites";
        }
    }

    // ---- Tags and to-do summaries ----

    private void InitializeSummaries()
    {
        _summaryRefresh = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) =>
        {
            _summaryRefresh!.Stop();
            RefreshSummaries();
        }, _dispatcher);
        _summaryRefresh.Stop();
        IndexUpdated += () =>
        {
            _summaryRefresh.Stop();
            _summaryRefresh.Start();
        };
    }

    public async void RefreshSummaries()
    {
        if (_search is not { } search)
        {
            return;
        }

        var showDone = ShowDoneTasks;
        var (tags, tasks) = await Task.Run(() =>
        {
            var tagCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var taskRows = new List<TaskRow>();
            foreach (var index in search.Indexes)
            {
                try
                {
                    foreach (var tag in index.Tags())
                    {
                        tagCounts[tag.Tag] = tagCounts.GetValueOrDefault(tag.Tag) + tag.Pages;
                    }

                    // Template pages hold placeholder tasks; they are not real to-dos.
                    var templates = Path.Combine(index.Root, PageTemplates.FolderName);
                    taskRows.AddRange(index.Tasks(showDone ? null : false).Where(t => !Core.Files.FileSystemMoves.IsSameOrInside(t.Path, templates)));
                }
                catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or ObjectDisposedException or InvalidOperationException)
                {
                    ErrorLog.Write(ex, "Reading tags and tasks");
                }
            }

            return (tagCounts.Select(t => new TagCount(t.Key, t.Value)).OrderBy(t => t.Tag, StringComparer.CurrentCultureIgnoreCase).ToList(), taskRows);
        });

        AllTags.Clear();
        foreach (var tag in tags)
        {
            AllTags.Add(tag);
        }

        Tasks.Clear();
        var entries = tasks.Select(t => new TaskEntry(t.Path, t.Title, t.Line, t.Done, t.Text, LocationOf(t.Path), ToggleTaskInPage))
            .OrderBy(t => t.GroupRank)
            .ThenByDescending(t => t.Info.Priority)
            .ThenBy(t => t.Info.Due ?? t.Info.Scheduled ?? DateOnly.MaxValue)
            .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(t => t.Line);
        foreach (var entry in entries)
        {
            Tasks.Add(entry);
        }

        TasksRefreshed?.Invoke();
    }

    /// <summary>Ticks or unticks a task in its page (through the open tab, or directly in the file).</summary>
    public void ToggleTaskInPage(TaskEntry task) =>
        UpdateTaskLine(task, line => TaskCompletion.Toggle(line, DateOnly.FromDateTime(DateTime.Today)));

    /// <summary>Moves a task's reminder to <paramref name="minutes"/> from now (snooze).</summary>
    public void SnoozeTask(TaskEntry task, int minutes) =>
        UpdateTaskLine(task, line => [TaskCompletion.SetReminder(line, DateTime.Now.AddMinutes(minutes))]);

    /// <summary>
    /// Rewrites a task's line (it may become several lines, as when a recurring task is completed): through the
    /// open tab as an undoable edit that autosaves, or directly in the file.
    /// </summary>
    private void UpdateTaskLine(TaskEntry task, Func<string, IReadOnlyList<string>> change)
    {
        if (OpenTabs.FirstOrDefault(t => t.IsAt(task.Path)) is { } tab)
        {
            var document = tab.Document;
            if (task.Line < 1 || task.Line > document.LineCount)
            {
                return;
            }

            var line = document.GetLineByNumber(task.Line);
            var text = document.GetText(line);
            var updated = string.Join(tab.Format.NewLine, change(text));
            if (updated != text)
            {
                document.Replace(line.Offset, line.Length, updated);
            }

            if (AutosaveEligible(tab))
            {
                SaveTab(tab, interactive: false);
            }
        }
        else
        {
            try
            {
                var loaded = TextDocumentFile.Load(task.Path);
                var lines = loaded.Text.Split('\n');
                if (task.Line > lines.Length)
                {
                    return;
                }

                var line = lines[task.Line - 1];
                var ending = line.EndsWith('\r') ? "\r" : "";
                lines[task.Line - 1] = string.Join(ending + "\n", change(line.TrimEnd('\r'))) + ending;
                History?.Record(task.Path, loaded.Text, Core.History.VersionReason.BeforeEditing);
                TextDocumentFile.Save(task.Path, string.Join('\n', lines), loaded.Format);
                _search?.Refresh(task.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _dialogs.ShowError($"Couldn't update the task:\n{ex.Message}");
                return;
            }
        }

        IndexUpdated?.Invoke();
    }

    public void OpenTask(TaskEntry task) => OpenDocument(task.Path, preview: false, line: task.Line);
}
