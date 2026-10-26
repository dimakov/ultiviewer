using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Ultiviewer.App.Services;
using Ultiviewer.Core.Notebooks;
using Ultiviewer.Core.OneNote;
using Ultiviewer.Core.OneNote.Store;

namespace Ultiviewer.App.ViewModels;

public enum ImportItemKind
{
    Group,
    Section,
    Page,
}

/// <summary>
/// A row of the import tree: a section group, section or page with a check box. Checking a row checks everything
/// under it; groups and sections show the combined state of their pages (indeterminate when mixed). A page's own
/// box stays independent of its subpages.
/// </summary>
public sealed partial class OneNoteImportItem(ImportItemKind kind, string name, OneNoteImportItem? parent) : ObservableObject
{
    private bool _updating;

    public ImportItemKind Kind { get; } = kind;

    public string Name { get; } = name;

    public OneNoteImportItem? Parent { get; } = parent;

    public ObservableCollection<OneNoteImportItem> Children { get; } = [];

    /// <summary>The section this row is or belongs to (sections and pages).</summary>
    public OneNoteSectionFile? Section { get; init; }

    /// <summary>A page's index in its section's pages.</summary>
    public int PageIndex { get; init; } = -1;

    /// <summary>What the conversion loses on this page, or why the section can't be read.</summary>
    public IReadOnlyList<string> Notes { get; set; } = [];

    /// <summary>Called after the user changes a check box anywhere in the tree.</summary>
    public Action? CheckChanged { get; init; }

    [ObservableProperty]
    private bool? _isChecked = true;

    [ObservableProperty]
    private string _detail = "";

    [ObservableProperty]
    private bool _hasWarning;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    public bool IsPage => Kind == ImportItemKind.Page;

    /// <summary>Segoe Fluent Icons: library for groups, folder for sections (as in the notebook tree), document for pages.</summary>
    public string Glyph => Kind switch
    {
        ImportItemKind.Group => "\uE8F1",
        ImportItemKind.Section => "\uE8B7",
        _ => "\uE8A5",
    };

    public IEnumerable<OneNoteImportItem> Descendants() => Children.SelectMany(c => c.Descendants().Prepend(c));

    partial void OnIsCheckedChanged(bool? value)
    {
        if (_updating)
        {
            return;
        }

        if (value is { } check)
        {
            foreach (var child in Children)
            {
                child.SetDown(check);
            }
        }

        Parent?.Recompute();
        CheckChanged?.Invoke();
    }

    /// <summary>A group or section takes the combined state of its contents (when it has any).</summary>
    internal void Recompute()
    {
        if (Kind != ImportItemKind.Page && Children.Count > 0)
        {
            var states = (Kind == ImportItemKind.Section ? Descendants() : Children).Select(c => c.IsChecked).Distinct().ToList();
            Set(states.Count == 1 ? states[0] : null);
        }

        Parent?.Recompute();
    }

    private void SetDown(bool check)
    {
        Set(check);
        foreach (var child in Children)
        {
            child.SetDown(check);
        }
    }

    private void Set(bool? value)
    {
        _updating = true;
        IsChecked = value;
        _updating = false;
    }
}

/// <summary>A converted page ready to show: its Markdown, written next to its pictures in a preview folder.</summary>
public sealed record OneNotePagePreview(string Markdown, string PagePath, string Root);

/// <summary>
/// The OneNote import window: the notebook's groups, sections and pages with check boxes, a preview of the
/// selected page exactly as it will be written (Markdown, rendered, and what is lost), and the import itself.
/// Sections are read in the background when the window opens, to count pages and find what can't be converted.
/// </summary>
public sealed partial class OneNoteImportViewModel : ObservableObject
{
    private const int CachedSections = 4;

    private readonly OneNoteSource _source;
    private readonly IDialogService _dialogs;
    private readonly Action<OneNoteImportResult> _imported;
    private readonly Dictionary<OneNoteSectionFile, OneNoteImportItem> _sections = [];
    private readonly Dictionary<(OneNoteSectionFile Section, int Index), OneNoteImportItem> _pages = [];
    private readonly List<(OneNoteSectionFile File, OneNoteSection Section)> _cache = [];
    private readonly CancellationTokenSource _closing = new();
    private readonly string _previewFolder = Path.Combine(Path.GetTempPath(), "Ultiviewer", "onenote-preview-" + Guid.NewGuid().ToString("N")[..12]);
    private CancellationTokenSource? _importing;
    private Task _scan = Task.CompletedTask;
    private Task _import = Task.CompletedTask;
    private int _previews;

    public OneNoteImportViewModel(OneNoteSource source, string sourcePath, string targetFolder, IDialogService dialogs, Action<OneNoteImportResult> imported)
    {
        _source = source;
        _dialogs = dialogs;
        _imported = imported;
        SourcePath = sourcePath;
        _targetFolder = targetFolder;
        _notebookName = source.Notebook.Name;
        foreach (var entry in source.Notebook.Entries)
        {
            Items.Add(CreateItem(entry, null));
        }

        foreach (var item in Items)
        {
            item.IsExpanded = true;
        }

        UpdateSummary();
    }

    public string SourcePath { get; }

    public string Title => $"Import from OneNote — {Path.GetFileName(Path.TrimEndingDirectorySeparator(SourcePath))}";

    public ObservableCollection<OneNoteImportItem> Items { get; } = [];

    /// <summary>Raised when the selected page's preview is ready to render.</summary>
    public event Action<OneNotePagePreview?>? PreviewReady;

    [ObservableProperty]
    private string _notebookName;

    [ObservableProperty]
    private string _targetFolder;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string _selection = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    private bool _isImporting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    private OneNoteImportResult? _result;

    [ObservableProperty]
    private OneNoteImportItem? _selected;

    [ObservableProperty]
    private string _pageMarkdown = "";

    /// <summary>What the selected page loses, the selected section's problem, or the import report.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _notes = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotes))]
    private string _notesHeading = "";

    public bool HasNotes => NotesHeading.Length > 0;

    public bool CanImport => !IsImporting && Result is null && SelectedPageCount() > 0;

    /// <summary>Reads every section once in the background: page lists, counts and conversion notes.</summary>
    public Task ScanAsync() => _scan = Scan();

    /// <summary>The background reading started by <see cref="ScanAsync"/>.</summary>
    public Task Scanning => _scan;

    private async Task Scan()
    {
        IsScanning = true;
        var token = _closing.Token;
        try
        {
            foreach (var (file, item) in _sections.ToList())
            {
                item.Detail = "reading…";
                var (section, notes) = await Task.Run(() =>
                {
                    var read = Read(file);
                    return (read, read.Pages.Select(p => OneNoteMarkdown.Convert(p, n => n).Notes).ToList());
                }, token);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                AddPages(item, file, section, notes);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsScanning = false;
            UpdateSummary();
        }
    }

    private void AddPages(OneNoteImportItem sectionItem, OneNoteSectionFile file, OneNoteSection section, List<IReadOnlyList<string>> notes)
    {
        if (section.Problem is { } problem)
        {
            sectionItem.Detail = "can't be read";
            sectionItem.HasWarning = true;
            sectionItem.Notes = [problem];
            sectionItem.IsChecked = false;
            return;
        }

        void Add(OneNoteImportItem parent, IReadOnlyList<OneNoteImporter.PageNode> nodes)
        {
            foreach (var node in nodes)
            {
                var title = OneNoteReader.DisplayTitle(node.Page) is { Length: > 0 } name ? name : "Untitled page";
                var page = new OneNoteImportItem(ImportItemKind.Page, title, parent)
                {
                    Section = file,
                    PageIndex = node.Index,
                    Notes = notes[node.Index],
                    HasWarning = notes[node.Index].Count > 0,
                    IsChecked = sectionItem.IsChecked != false,
                    IsExpanded = true,
                    CheckChanged = UpdateSummary,
                };
                parent.Children.Add(page);
                _pages[(file, node.Index)] = page;
                Add(page, node.Children);
            }
        }

        Add(sectionItem, OneNoteImporter.Arrange(section.Pages));
        var warnings = notes.Count(n => n.Count > 0);
        sectionItem.Detail = Plural(section.Pages.Count, "page") + (warnings > 0 ? $", {warnings} with notes" : "");
        sectionItem.HasWarning = warnings > 0;
        sectionItem.Recompute();
        UpdateSummary();
    }

    partial void OnSelectedChanged(OneNoteImportItem? value)
    {
        if (Result is not null)
        {
            return; // the notes show the import report
        }

        if (value is { Kind: ImportItemKind.Page, Section: { } file })
        {
            _ = ShowPageAsync(value, file, value.PageIndex);
            return;
        }

        PageMarkdown = "";
        PreviewReady?.Invoke(null);
        Notes = value?.Notes ?? [];
        NotesHeading = Notes.Count > 0 ? "This section can't be imported" : "";
    }

    private async Task ShowPageAsync(OneNoteImportItem item, OneNoteSectionFile file, int index)
    {
        var folder = Path.Combine(_previewFolder, (++_previews).ToString());
        try
        {
            var (converted, pagePath) = await Task.Run(() =>
            {
                var page = Read(file).Pages[index];
                var path = Path.Combine(folder, FileNameRules.MakeValid(OneNoteReader.DisplayTitle(page), "Untitled page") + NotebookScanner.PageExtension);
                var assets = PageAssets.FolderFor(path);
                var result = OneNoteMarkdown.Convert(page, asset => PageAssets.RelativeUrl(path, Path.Combine(assets, asset)));
                Directory.CreateDirectory(assets);
                foreach (var asset in result.Assets)
                {
                    File.WriteAllBytes(Path.Combine(assets, asset.Name), asset.Data);
                }

                return (result, path);
            });
            if (Selected != item)
            {
                return;
            }

            PageMarkdown = converted.Markdown;
            Notes = converted.Notes;
            NotesHeading = Notes.Count > 0 ? "Not imported exactly" : "";
            PreviewReady?.Invoke(new OneNotePagePreview(converted.Markdown, pagePath, folder));
            DeleteOldPreviews(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OneNoteFormatException or ArgumentOutOfRangeException)
        {
            PageMarkdown = "";
            Notes = [ex.Message];
            NotesHeading = "The page can't be shown";
        }
    }

    public void PickTargetFolder()
    {
        if (_dialogs.PickFolder("Choose where to put the imported notebook", TargetFolder) is { } folder)
        {
            TargetFolder = folder;
        }
    }

    /// <summary>Writes the checked sections and pages; the notebook is opened through the callback.</summary>
    public Task ImportAsync() => _import = Import();

    private async Task Import()
    {
        if (!CanImport)
        {
            return;
        }

        if (FileNameRules.Validate(NotebookName.Trim()) is { } invalid)
        {
            Status = invalid;
            return;
        }

        // The choices are taken now; the import runs on a background thread.
        var excludedSections = _sections.Where(s => s.Value.IsChecked == false).Select(s => s.Key).ToHashSet();
        var excludedPages = _pages.Where(p => p.Value.IsChecked != true).Select(p => p.Key).ToHashSet();
        var options = new OneNoteImportOptions
        {
            IncludeSection = file => !excludedSections.Contains(file),
            IncludePage = (file, index) => !excludedPages.Contains((file, index)),
            ReadSection = Read,
        };
        var name = NotebookName.Trim();
        var target = TargetFolder;

        IsImporting = true;
        _importing = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        var progress = new Progress<string>(where => Status = $"Importing {where}…");
        try
        {
            var result = await Task.Run(() => OneNoteImporter.Import(_source.Notebook, target, name, options, progress, _importing.Token));
            Result = result;
            Status = $"Imported {Plural(result.Pages, "page")} in {Plural(result.Sections, "section")} into {result.Folder}";
            NotesHeading = result.Issues.Count > 0 ? "Import report: not imported exactly" : "Import report: everything was converted";
            Notes = [.. result.Issues.Select(i => $"{i.Where}: {i.Message}")];
            PageMarkdown = "";
            PreviewReady?.Invoke(null);
            _imported(result);
        }
        catch (OperationCanceledException)
        {
            Status = "Import cancelled. Nothing was written.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotebookOperationException)
        {
            Status = "Couldn't import: " + ex.Message;
        }
        finally
        {
            IsImporting = false;
            _importing.Dispose();
            _importing = null;
        }
    }

    public void CancelImport() => _importing?.Cancel();

    /// <summary>Stops reading and importing, then deletes the preview files and the package's temporary folder.</summary>
    public async Task CloseAsync()
    {
        _closing.Cancel();
        try
        {
            await Task.WhenAll(_scan, _import);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
        }

        _source.Dispose();
        TryDelete(_previewFolder);
    }

    private OneNoteImportItem CreateItem(OneNoteEntry entry, OneNoteImportItem? parent)
    {
        switch (entry)
        {
            case OneNoteSectionGroup group:
            {
                var item = new OneNoteImportItem(ImportItemKind.Group, group.Name, parent) { CheckChanged = UpdateSummary, IsExpanded = true };
                foreach (var child in group.Entries)
                {
                    item.Children.Add(CreateItem(child, item));
                }

                item.Detail = Plural(OneNoteSource.CountSections(group.Entries), "section");
                return item;
            }

            case OneNoteSectionFile file:
            {
                var item = new OneNoteImportItem(ImportItemKind.Section, file.Name, parent) { Section = file, CheckChanged = UpdateSummary, Detail = "waiting…" };
                _sections[file] = item;
                return item;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(entry));
        }
    }

    /// <summary>A section, from the cache of the last few read (preview and import reuse them).</summary>
    private OneNoteSection Read(OneNoteSectionFile file)
    {
        lock (_cache)
        {
            var index = _cache.FindIndex(c => c.File == file);
            if (index >= 0)
            {
                var hit = _cache[index];
                _cache.RemoveAt(index);
                _cache.Add(hit);
                return hit.Section;
            }
        }

        var section = OneNoteReader.ReadSection(file.Path);
        lock (_cache)
        {
            _cache.Add((file, section));
            if (_cache.Count > CachedSections)
            {
                _cache.RemoveAt(0);
            }
        }

        return section;
    }

    private int SelectedPageCount() => _pages.Count(p => p.Value.IsChecked == true);

    private void UpdateSummary()
    {
        var sections = _sections.Values.Count(s => s.IsChecked != false);
        Selection = IsScanning || _pages.Count == 0 && _sections.Count > 0
            ? $"{sections} of {Plural(_sections.Count, "section")} selected · reading pages…"
            : $"{sections} of {Plural(_sections.Count, "section")}, {SelectedPageCount()} of {Plural(_pages.Count, "page")} selected";
        OnPropertyChanged(nameof(CanImport));
    }

    private void DeleteOldPreviews(string keep)
    {
        foreach (var folder in Directory.EnumerateDirectories(_previewFolder))
        {
            if (!string.Equals(folder, keep, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(folder);
            }
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The preview may still be showing a picture; the folder is in %TEMP%.
        }
    }

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";
}
