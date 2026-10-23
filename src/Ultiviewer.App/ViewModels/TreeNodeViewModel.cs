using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Ultiviewer.Core.Notebooks;
using Ultiviewer.Core.Settings;

namespace Ultiviewer.App.ViewModels;

/// <summary>
/// One node in the notebook tree. Rescans produce new <see cref="NotebookNode"/> snapshots; <see cref="Apply"/>
/// merges them into the existing view models by path so expansion and selection survive.
/// </summary>
public sealed partial class TreeNodeViewModel : ObservableObject
{
    public TreeNodeViewModel(NotebookNode node, TreeNodeViewModel? parent, ISet<string> expanded)
    {
        Parent = parent;
        Node = node;
        _name = node.Name;
        _color = node.Color;
        _isExpanded = expanded.Contains(node.Path);
        SyncChildren(node.Children, expanded);
    }

    public TreeNodeViewModel? Parent { get; }

    /// <summary>Settings entry for notebook roots.</summary>
    public NotebookEntry? Entry { get; init; }

    public NotebookNode Node { get; private set; }

    public NodeKind Kind => Node.Kind;

    public string Path => Node.Path;

    public ObservableCollection<TreeNodeViewModel> Children { get; } = [];

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasColor))]
    private string? _color;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editText = "";

    public bool HasColor => Color is not null;

    public string Glyph => Kind switch
    {
        NodeKind.Notebook => "", // Library
        NodeKind.Section => "",  // Folder
        NodeKind.Page => "",     // Document
        _ => "",                 // Page
    };

    public TreeNodeViewModel Root => Parent?.Root ?? this;

    public void Apply(NotebookNode node, ISet<string> expanded)
    {
        var kindChanged = node.Kind != Node.Kind;
        Node = node;
        Name = node.Name;
        Color = node.Color;
        OnPropertyChanged(nameof(Path));
        if (kindChanged)
        {
            OnPropertyChanged(nameof(Kind));
            OnPropertyChanged(nameof(Glyph));
        }

        SyncChildren(node.Children, expanded);
    }

    public IEnumerable<TreeNodeViewModel> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var node in child.DescendantsAndSelf())
            {
                yield return node;
            }
        }
    }

    public TreeNodeViewModel? Find(string path) =>
        DescendantsAndSelf().FirstOrDefault(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase));

    public void ExpandAncestors()
    {
        for (var node = Parent; node is not null; node = node.Parent)
        {
            node.IsExpanded = true;
        }
    }

    private void SyncChildren(IReadOnlyList<NotebookNode> nodes, ISet<string> expanded)
    {
        var existing = Children.ToDictionary(c => c.Path, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (existing.Remove(node.Path, out var vm) && vm.Kind == node.Kind)
            {
                vm.Apply(node, expanded);
                var index = Children.IndexOf(vm);
                if (index != i)
                {
                    Children.Move(index, i);
                }
            }
            else
            {
                if (vm is not null)
                {
                    Children.Remove(vm);
                }

                Children.Insert(i, new TreeNodeViewModel(node, this, expanded));
            }
        }

        while (Children.Count > nodes.Count)
        {
            Children.RemoveAt(Children.Count - 1);
        }
    }
}
