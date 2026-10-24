using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Ultiviewer.Core.Files;
using Ultiviewer.Core.History;

namespace Ultiviewer.App.ViewModels;

/// <summary>One entry in the history list: a stored version, or the file on disk while the tab has unsaved changes.</summary>
public sealed class VersionItem
{
    public required string Label { get; init; }

    public required string Detail { get; init; }

    public required Func<string?> Load { get; init; }

    public string? Id { get; init; }
}

/// <summary>The history window: a tab's versions, newest first, compared with the tab's current text.</summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly VersionHistory _history;
    private readonly Dictionary<string, string?> _texts = [];

    public HistoryViewModel(MainViewModel main, DocumentTabViewModel tab, VersionHistory history)
    {
        _main = main;
        _history = history;
        Tab = tab;
        Refresh();
    }

    public DocumentTabViewModel Tab { get; }

    public string Title => $"History of {Tab.Title}";

    public ObservableCollection<VersionItem> Versions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRestore))]
    private VersionItem? _selected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVersion))]
    private bool _showChanges = true;

    [ObservableProperty]
    private string _summary = "";

    public bool ShowVersion
    {
        get => !ShowChanges;
        set => ShowChanges = !value;
    }

    public bool CanRestore => Selected is not null;

    /// <summary>The text on the right: the diff (when comparing) or the selected version.</summary>
    public string ViewText { get; private set; } = "";

    /// <summary>The diff kind of each line of <see cref="ViewText"/>; empty when not comparing.</summary>
    public IReadOnlyList<DiffKind> LineKinds { get; private set; } = [];

    /// <summary>0-based line of the first change, to scroll to.</summary>
    public int FirstChangeLine { get; private set; }

    public event Action? ViewChanged;

    /// <summary>Reloads the list (after a restore or a save), keeping the selection when it still exists.</summary>
    public void Refresh()
    {
        var selectedId = Selected?.Id;
        Versions.Clear();
        if (Tab.Path is not { } path)
        {
            return;
        }

        if (Tab.IsDirty && File.Exists(path))
        {
            Versions.Add(new VersionItem
            {
                Label = "File on disk",
                Detail = "The saved file; your unsaved changes are compared with it",
                Load = () => TryLoad(path),
            });
        }

        foreach (var version in _history.List(path))
        {
            Versions.Add(new VersionItem
            {
                Id = version.Id,
                Label = When(version.TimeUtc.ToLocalTime(), DateTime.Now),
                Detail = $"{Describe(version.Reason)} · {Size(version.Length)}",
                Load = () => Text(path, version.Id),
            });
        }

        Selected = Versions.FirstOrDefault(v => v.Id is not null && v.Id == selectedId) ?? Versions.FirstOrDefault();
    }

    /// <summary>Recomputes the comparison, for example after the tab's text changed.</summary>
    public void UpdateView()
    {
        var old = Selected?.Load();
        FirstChangeLine = 0;
        if (old is null)
        {
            ViewText = "";
            LineKinds = [];
            Summary = Selected is null ? "No versions yet: they are kept as you save." : "This version can't be read.";
        }
        else if (ShowChanges)
        {
            var diff = LineDiff.Compute(old, Tab.Document.Text);
            ViewText = string.Join("\n", diff.Select(d => d.Text));
            LineKinds = [.. diff.Select(d => d.Kind)];
            FirstChangeLine = Math.Max(0, LineKinds.ToList().FindIndex(k => k != DiffKind.Same));
            var (added, removed) = LineDiff.Count(diff);
            Summary = added + removed == 0
                ? "Same as the current text."
                : $"Since this version: {Lines(added)} added (green), {Lines(removed)} removed (red).";
        }
        else
        {
            ViewText = old;
            LineKinds = [];
            Summary = $"{Selected!.Label} · {Selected.Detail}";
        }

        ViewChanged?.Invoke();
    }

    /// <summary>The selected version's full text, or null.</summary>
    public string? SelectedText() => Selected?.Load();

    /// <summary>Puts the selected version into the tab (one undoable edit; the replaced text stays in history).</summary>
    public void Restore()
    {
        if (SelectedText() is { } text)
        {
            _main.RestoreVersion(Tab, text);
            Refresh();
            UpdateView();
        }
    }

    partial void OnSelectedChanged(VersionItem? value) => UpdateView();

    partial void OnShowChangesChanged(bool value) => UpdateView();

    private string? Text(string path, string id)
    {
        if (!_texts.TryGetValue(id, out var text))
        {
            text = _history.Read(path, id);
            _texts[id] = text;
        }

        return text;
    }

    private string? TryLoad(string path)
    {
        try
        {
            return TextDocumentFile.Load(path, _main.Settings.DefaultLineEnding).Text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string When(DateTime local, DateTime now)
    {
        var time = local.ToString("HH:mm", CultureInfo.CurrentCulture);
        return (now.Date - local.Date).Days switch
        {
            0 => $"Today {time}",
            1 => $"Yesterday {time}",
            < 7 and > 1 => $"{local.ToString("dddd", CultureInfo.CurrentCulture)} {time}",
            _ => local.ToString("d MMM yyyy", CultureInfo.CurrentCulture) + " " + time,
        };
    }

    private static string Describe(VersionReason reason) => reason switch
    {
        VersionReason.BeforeEditing => "Before this session's edits",
        VersionReason.Saved => "Saved",
        VersionReason.ChangedOnDisk => "Changed by another program",
        VersionReason.BeforeOutsideChange => "Before another program changed it",
        VersionReason.Conflict => "Your unsaved text during a conflict",
        VersionReason.BeforeRestore => "Before restoring an older version",
        _ => reason.ToString(),
    };

    private static string Size(int characters) => characters < 1024 ? $"{characters} chars" : $"{characters / 1024.0:0.#} K chars";

    private static string Lines(int count) => count == 1 ? "1 line" : $"{count} lines";
}
