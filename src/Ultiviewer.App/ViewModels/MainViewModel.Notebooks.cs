using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ultiviewer.App.Services;
using Ultiviewer.Core.Notebooks;
using Ultiviewer.Core.Settings;

namespace Ultiviewer.App.ViewModels;

public enum DropPosition
{
    Before,
    After,
    Inside,
}

/// <summary>Notebook tree: loading, watching, and create/rename/move/delete commands.</summary>
public sealed partial class MainViewModel
{
    private readonly Dictionary<TreeNodeViewModel, NotebookWatcher> _watchers = [];
    private readonly Dictionary<TreeNodeViewModel, int> _refreshVersions = [];
    private HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    private string? _renamingNewPage;

    public ObservableCollection<TreeNodeViewModel> Notebooks { get; } = [];

    [ObservableProperty]
    private TreeNodeViewModel? _selectedNode;

    public bool HasNotebooks => Notebooks.Count > 0;

    public static IReadOnlyList<(string Name, string? Hex)> SectionColors { get; } =
    [
        ("Blue", "#3A7BD5"), ("Teal", "#1E9E9E"), ("Green", "#3E9B4F"), ("Yellow", "#C9A227"),
        ("Orange", "#E07B39"), ("Red", "#D64545"), ("Pink", "#C2478F"), ("Purple", "#7E57C2"),
        ("Gray", "#7A7A7A"), ("None", null),
    ];

    private void LoadNotebooks()
    {
        _expanded = new HashSet<string>(Session.ExpandedPaths, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Settings.Notebooks.ToList())
        {
            AddNotebookNode(entry);
        }

        Notebooks.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNotebooks));
        OnPropertyChanged(nameof(HasNotebooks));
    }

    private void RestoreSelection()
    {
        if (Session.SelectedPath is { } path && FindNode(path) is { } node)
        {
            node.ExpandAncestors();
            node.IsSelected = true;
        }
    }

    private void CaptureNotebookState()
    {
        Session.ExpandedPaths = Notebooks.SelectMany(n => n.DescendantsAndSelf()).Where(n => n.IsExpanded).Select(n => n.Path).ToList();
        Session.SelectedPath = SelectedNode?.Path;
    }

    private TreeNodeViewModel AddNotebookNode(NotebookEntry entry)
    {
        var root = new TreeNodeViewModel(NotebookScanner.ScanNotebook(entry.Path, entry.DisplayName), null, _expanded) { Entry = entry };
        Notebooks.Add(root);
        var watcher = new NotebookWatcher(entry.Path);
        watcher.StructureChanged += () => _dispatcher.InvokeAsync(() => RefreshNotebookInBackground(root));
        watcher.FileChanged += path => _dispatcher.InvokeAsync(() => OnFileChangedOnDisk(path));
        watcher.FullRescanRequested += () => _dispatcher.InvokeAsync(() => OnNotebookRescanRequested(root));
        _watchers[root] = watcher;
        return root;
    }

    /// <summary>Called on the UI thread when a file inside a notebook changes on disk.</summary>
    partial void OnFileChangedOnDisk(string path);

    partial void OnNotebookRescanRequested(TreeNodeViewModel root);

    /// <summary>Called after a page, section or file moved, so open documents can follow it.</summary>
    partial void OnItemMoved(string oldPath, string newPath);

    /// <summary>Called after items were deleted, so open documents can close.</summary>
    partial void OnItemDeleted(string path);

    /// <summary>Called when a new page has been created and named; opens it for editing.</summary>
    partial void OnNewPageReady(string path);

    private void DisposeWatchers()
    {
        foreach (var watcher in _watchers.Values)
        {
            watcher.Dispose();
        }

        _watchers.Clear();
    }

    public TreeNodeViewModel? FindNode(string path)
    {
        foreach (var root in Notebooks)
        {
            if (root.Find(path) is { } node)
            {
                return node;
            }
        }

        return null;
    }

    /// <summary>The notebook that contains <paramref name="path"/>, if any.</summary>
    public TreeNodeViewModel? NotebookOf(string path) =>
        Notebooks.FirstOrDefault(root => Core.Files.FileSystemMoves.IsSameOrInside(path, root.Path));

    public TreeNodeViewModel? SelectPath(string path)
    {
        var node = FindNode(path);
        if (node is not null)
        {
            // A just-created node has no tree item yet, so the TreeView can't unselect the old one itself; left
            // selected, the old item would take the selection back when a refresh moves it.
            if (SelectedNode is { } previous && previous != node)
            {
                previous.IsSelected = false;
            }

            node.ExpandAncestors();
            node.IsSelected = true;
            SelectedNode = node;
        }

        return node;
    }

    /// <summary>Rescans a notebook now (after the app changed it) and merges the result into the tree.</summary>
    public void RefreshNotebook(TreeNodeViewModel root)
    {
        _refreshVersions[root] = _refreshVersions.GetValueOrDefault(root) + 1;
        root.Apply(NotebookScanner.ScanNotebook(root.Path, root.Entry?.DisplayName), _expanded);
    }

    private async void RefreshNotebookInBackground(TreeNodeViewModel root)
    {
        if (!Notebooks.Contains(root))
        {
            return;
        }

        var version = _refreshVersions[root] = _refreshVersions.GetValueOrDefault(root) + 1;
        var path = root.Path;
        var displayName = root.Entry?.DisplayName;
        var node = await Task.Run(() => NotebookScanner.ScanNotebook(path, displayName));
        if (_refreshVersions.GetValueOrDefault(root) == version && Notebooks.Contains(root))
        {
            root.Apply(node, _expanded);
        }
    }

    [RelayCommand]
    private void Refresh()
    {
        foreach (var root in Notebooks)
        {
            RefreshNotebook(root);
        }

        StatusMessage = "Notebooks refreshed";
    }

    [RelayCommand]
    private void NewNotebook()
    {
        var parent = AppPaths.DefaultNotebooksDirectory;
        var first = Notebooks.Count == 0;
        var suggested = NotebookOperations.UniqueName(parent, first ? "My Notebook" : "Notebook");
        var name = _dialogs.Prompt("New notebook", $"Name of the new notebook (created in {parent}):", suggested, n =>
            FileNameRules.Validate(n) ?? (Directory.Exists(Path.Combine(parent, n)) ? "A folder with this name already exists." : null));
        if (name is null)
        {
            return;
        }

        var folder = Path.Combine(parent, name);
        string? page = null;
        if (!TryRun(() =>
            {
                Directory.CreateDirectory(folder);
                page = first
                    ? _ops.CreatePage(folder, "Welcome", WelcomePage.Text)
                    : _ops.CreatePage(folder, "Untitled page");
            }))
        {
            return;
        }

        OpenNotebook(folder);
        if (page is not null && SelectPath(page) is { } node && !first)
        {
            BeginRename(node, isNewPage: true);
        }
    }

    [RelayCommand]
    private void OpenNotebookFolder()
    {
        if (_dialogs.PickFolder("Open a folder as a notebook") is { } folder)
        {
            OpenNotebook(folder);
        }
    }

    public TreeNodeViewModel OpenNotebook(string folder)
    {
        var existing = Notebooks.FirstOrDefault(n => Core.Files.FileSystemMoves.IsSamePath(n.Path, folder));
        if (existing is not null)
        {
            existing.IsSelected = true;
            return existing;
        }

        var entry = new NotebookEntry { Path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) };
        Settings.Notebooks.Add(entry);
        Settings.Save();
        var root = AddNotebookNode(entry);
        root.IsExpanded = true;
        root.IsSelected = true;
        return root;
    }

    [RelayCommand]
    private void CloseNotebook(TreeNodeViewModel? node)
    {
        if ((node ?? SelectedNode)?.Root is not { Entry: { } entry } root)
        {
            return;
        }

        if (_watchers.Remove(root, out var watcher))
        {
            watcher.Dispose();
        }

        Notebooks.Remove(root);
        Settings.Notebooks.Remove(entry);
        Settings.Save();
        StatusMessage = $"Closed notebook \"{root.Name}\". Its files were not changed.";
    }

    [RelayCommand]
    private void NewPage(TreeNodeViewModel? node)
    {
        if (TargetNode(node) is not { } target)
        {
            return;
        }

        var (container, after) = target.Kind is NodeKind.Notebook or NodeKind.Section
            ? (target.Path, (string?)null)
            : (target.Node.ParentFolder, target.Node.EntryName);
        CreatePageIn(target.Root, container, after);
    }

    [RelayCommand]
    private void NewSubpage(TreeNodeViewModel? node)
    {
        if ((node ?? SelectedNode) is { Kind: NodeKind.Page } page)
        {
            page.IsExpanded = true;
            CreatePageIn(page.Root, page.Node.ChildFolder!, null);
        }
    }

    private void CreatePageIn(TreeNodeViewModel root, string container, string? after)
    {
        var title = NotebookOperations.UniqueName(container, "Untitled page");
        string? path = null;
        if (TryRun(() => path = _ops.CreatePage(container, title, "", after)))
        {
            RefreshNotebook(root);
            if (SelectPath(path!) is { } created)
            {
                BeginRename(created, isNewPage: true);
            }
        }
    }

    [RelayCommand]
    private void NewSection(TreeNodeViewModel? node)
    {
        if (TargetNode(node) is not { } target)
        {
            return;
        }

        // A new section goes next to the selected section, or next to the section holding the selected page.
        var anchor = target;
        while (anchor.Kind is NodeKind.Page or NodeKind.File)
        {
            anchor = anchor.Parent!;
        }

        var (container, after) = anchor.Kind == NodeKind.Notebook
            ? (anchor.Path, (string?)null)
            : (anchor.Node.ParentFolder, anchor.Node.EntryName);
        CreateSectionIn(target.Root, container, after);
    }

    [RelayCommand]
    private void NewSubsection(TreeNodeViewModel? node)
    {
        if ((node ?? SelectedNode) is { Kind: NodeKind.Section or NodeKind.Notebook } section)
        {
            section.IsExpanded = true;
            CreateSectionIn(section.Root, section.Path, null);
        }
    }

    private void CreateSectionIn(TreeNodeViewModel root, string container, string? after)
    {
        var name = NotebookOperations.UniqueName(container, "New section");
        string? path = null;
        if (TryRun(() => path = _ops.CreateSection(container, name, after)))
        {
            RefreshNotebook(root);
            if (SelectPath(path!) is { } created)
            {
                BeginRename(created, isNewPage: false);
            }
        }
    }

    [RelayCommand]
    private void Rename(TreeNodeViewModel? node)
    {
        if ((node ?? SelectedNode) is { } target)
        {
            BeginRename(target, isNewPage: false);
        }
    }

    private void BeginRename(TreeNodeViewModel node, bool isNewPage)
    {
        _renamingNewPage = isNewPage ? node.Path : null;
        node.EditText = node.Name;
        node.IsEditing = true;
    }

    /// <summary>Ends inline renaming (Enter, Esc or focus loss) and applies the new name.</summary>
    public void CommitRename(TreeNodeViewModel node, bool cancel)
    {
        if (!node.IsEditing)
        {
            return;
        }

        node.IsEditing = false;
        var newName = node.EditText.Trim();
        var wasNewPage = string.Equals(_renamingNewPage, node.Path, StringComparison.OrdinalIgnoreCase);
        _renamingNewPage = null;

        var finalPath = node.Path;
        if (!cancel && newName != node.Name)
        {
            if (node.Kind == NodeKind.Notebook)
            {
                node.Entry!.DisplayName = newName;
                node.Name = newName;
                Settings.Save();
                return;
            }

            var oldPath = node.Path;
            var before = node.Node;
            string? newPath = null;
            if (TryRun(() => newPath = _ops.Rename(node.Node, newName)))
            {
                OnItemMoved(oldPath, newPath!);
                RefreshNotebook(node.Root);
                SelectPath(newPath!);
                finalPath = newPath!;
                UpdateLinksAfterMove(before, newPath!);
            }
        }

        if (wasNewPage)
        {
            OnNewPageReady(finalPath);
        }
    }

    [RelayCommand]
    private void Delete(TreeNodeViewModel? node)
    {
        if ((node ?? SelectedNode) is not { } target)
        {
            return;
        }

        if (target.Kind == NodeKind.Notebook)
        {
            CloseNotebook(target);
            return;
        }

        var what = target.Kind switch
        {
            NodeKind.Section => $"the section \"{target.Name}\" and everything in it",
            NodeKind.Page when target.Children.Count > 0 => $"the page \"{target.Name}\" and its subpages",
            NodeKind.Page => $"the page \"{target.Name}\"",
            _ => $"\"{target.Name}\"",
        };
        if (!_dialogs.Confirm($"Move {what} to the Recycle Bin?"))
        {
            return;
        }

        var parent = target.Parent;
        var index = parent?.Children.IndexOf(target) ?? -1;
        var removed = target.DescendantsAndSelf().Select(n => n.Path).ToList();
        if (TryRun(() => _ops.Delete(target.Node)))
        {
            foreach (var path in removed)
            {
                OnItemDeleted(path);
            }

            RefreshNotebook(target.Root);
            if (parent is not null)
            {
                var next = parent.Children.Count == 0 ? parent : parent.Children[Math.Clamp(index, 0, parent.Children.Count - 1)];
                next.IsSelected = true;
            }
        }
    }

    public void SetColor(TreeNodeViewModel node, string? color)
    {
        if (node.Kind is NodeKind.Notebook or NodeKind.Section && TryRun(() => NotebookOperations.SetColor(node.Path, color)))
        {
            node.Color = color;
        }
    }

    [RelayCommand]
    private void RevealInExplorer(TreeNodeViewModel? node)
    {
        if ((node ?? SelectedNode) is { } target)
        {
            ShellService.RevealInExplorer(target.Path);
        }
    }

    [RelayCommand]
    private void CopyPath(TreeNodeViewModel? node)
    {
        if ((node ?? SelectedNode) is { } target)
        {
            Clipboard.SetText(target.Path);
            StatusMessage = "Path copied";
        }
    }

    /// <summary>Drag and drop in the tree: reorder, move between sections, nest pages, reorder notebooks.</summary>
    public void MoveNode(TreeNodeViewModel node, TreeNodeViewModel target, DropPosition position)
    {
        if (node == target)
        {
            return;
        }

        if (node.Kind == NodeKind.Notebook)
        {
            if (target.Kind == NodeKind.Notebook)
            {
                MoveNotebook(node, target, position);
            }

            return;
        }

        if (target.Kind == NodeKind.Notebook || target.Kind == NodeKind.File && position == DropPosition.Inside)
        {
            position = target.Kind == NodeKind.Notebook ? DropPosition.Inside : DropPosition.After;
        }

        string container;
        int? index = null;
        if (position == DropPosition.Inside)
        {
            container = target.Node.ChildFolder!;
        }
        else
        {
            container = target.Node.ParentFolder;
            var entries = NotebookScanner.ListEntries(container);
            var targetIndex = entries.FindIndex(e => string.Equals(e, target.Node.EntryName, StringComparison.OrdinalIgnoreCase));
            index = targetIndex < 0 ? null : targetIndex + (position == DropPosition.After ? 1 : 0);
        }

        var oldPath = node.Path;
        var oldRoot = node.Root;
        var before = node.Node;
        string? newPath = null;
        if (TryRun(() => newPath = _ops.Move(node.Node, container, index)))
        {
            if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
            {
                OnItemMoved(oldPath, newPath!);
                UpdateLinksAfterMove(before, newPath!);
            }

            if (position == DropPosition.Inside)
            {
                target.IsExpanded = true;
            }

            RefreshNotebook(oldRoot);
            if (target.Root != oldRoot)
            {
                RefreshNotebook(target.Root);
            }

            SelectPath(newPath!);
        }
    }

    /// <summary>Copies files or folders dropped from Explorer into the notebook.</summary>
    public void ImportFiles(IEnumerable<string> sources, TreeNodeViewModel target, DropPosition position)
    {
        var container = target.Kind == NodeKind.Notebook || position == DropPosition.Inside && target.Node.ChildFolder is not null
            ? target.Node.ChildFolder!
            : target.Node.ParentFolder;
        string? last = null;
        TryRun(() =>
        {
            Directory.CreateDirectory(container);
            foreach (var source in sources)
            {
                var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
                var destination = UniquePath(container, name);
                if (Core.Files.FileSystemMoves.IsSameOrInside(container, source))
                {
                    continue;
                }

                if (Directory.Exists(source))
                {
                    Core.Files.FileSystemMoves.CopyDirectory(source, destination);
                }
                else if (File.Exists(source))
                {
                    File.Copy(source, destination);
                }

                last = destination;
            }
        });

        RefreshNotebook(target.Root);
        if (last is not null)
        {
            SelectPath(last);
        }
    }

    private static string UniquePath(string folder, string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        var candidate = Path.Combine(folder, name);
        for (var n = 2; File.Exists(candidate) || Directory.Exists(candidate); n++)
        {
            candidate = Path.Combine(folder, $"{stem} {n}{extension}");
        }

        return candidate;
    }

    private void MoveNotebook(TreeNodeViewModel node, TreeNodeViewModel target, DropPosition position)
    {
        var from = Notebooks.IndexOf(node);
        var to = Notebooks.IndexOf(target) + (position == DropPosition.After ? 1 : 0);
        if (to > from)
        {
            to--;
        }

        Notebooks.Move(from, Math.Clamp(to, 0, Notebooks.Count - 1));
        Settings.Notebooks.Remove(node.Entry!);
        Settings.Notebooks.Insert(Notebooks.IndexOf(node), node.Entry!);
        Settings.Save();
    }

    private TreeNodeViewModel? TargetNode(TreeNodeViewModel? node)
    {
        var target = node ?? SelectedNode ?? Notebooks.FirstOrDefault();
        if (target is null)
        {
            _dialogs.ShowInfo("Create or open a notebook first.");
        }

        return target;
    }
}
