using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Ultiviewer.Core.Settings;

namespace Ultiviewer.App.ViewModels;

/// <summary>
/// One editor pane's tabs. The window has a first group and, when split, a second; the same document can be open
/// in both, each with its own caret, scroll position and (in the second group) view mode.
/// </summary>
public sealed partial class EditorGroup : ObservableObject
{
    private readonly Dictionary<DocumentTabViewModel, ViewMode> _viewModes = [];

    public EditorGroup(int index)
    {
        Index = index;
        Tabs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTabs));
    }

    /// <summary>0 for the first group, 1 for the second.</summary>
    public int Index { get; }

    public ObservableCollection<DocumentTabViewModel> Tabs { get; } = [];

    public bool HasTabs => Tabs.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveViewMode))]
    private DocumentTabViewModel? _activeTab;

    [ObservableProperty]
    private bool _isFocused;

    /// <summary>The active tab's view mode in this group, or null when it isn't Markdown (the mode buttons hide).</summary>
    public ViewMode? ActiveViewMode => ActiveTab is { IsMarkdown: true } tab ? ModeOf(tab) : null;

    /// <summary>A tab's view mode here: the second group can show a document differently from the first.</summary>
    public ViewMode ModeOf(DocumentTabViewModel tab) => _viewModes.TryGetValue(tab, out var mode) ? mode : tab.ViewMode;

    /// <summary>Raised when this group shows a tab in another mode.</summary>
    public event Action<DocumentTabViewModel>? ModeChanged;

    /// <summary>Sets the mode for this group only (the first group's mode is the document's own).</summary>
    public void SetMode(DocumentTabViewModel tab, ViewMode mode)
    {
        if (Index == 0)
        {
            tab.ViewMode = mode;
        }
        else
        {
            _viewModes[tab] = mode;
        }

        OnPropertyChanged(nameof(ActiveViewMode));
        ModeChanged?.Invoke(tab);
    }

    /// <summary>Starts showing a tab with its current mode.</summary>
    public void Adopt(DocumentTabViewModel tab)
    {
        if (Index > 0)
        {
            _viewModes.TryAdd(tab, tab.ViewMode);
        }
    }

    public void Forget(DocumentTabViewModel tab) => _viewModes.Remove(tab);

    partial void OnActiveTabChanged(DocumentTabViewModel? oldValue, DocumentTabViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnTabPropertyChanged;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnTabPropertyChanged;
        }
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentTabViewModel.ViewMode) or nameof(DocumentTabViewModel.IsMarkdown) or nameof(DocumentTabViewModel.Language))
        {
            OnPropertyChanged(nameof(ActiveViewMode));
        }
    }
}
