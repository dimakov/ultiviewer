using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Ultiviewer.App.ViewModels;

/// <summary>
/// Two editors side by side (or stacked): a first and a second <see cref="EditorGroup"/>. <see cref="ActiveTab"/>
/// is always the focused group's active tab, so commands keep acting on "the document you are working on";
/// <see cref="OpenTabs"/> is every open document once, for saving, disk checks, search and the session.
/// </summary>
public partial class MainViewModel
{
    public EditorGroup Primary { get; } = new(0);

    public EditorGroup Secondary { get; } = new(1);

    [ObservableProperty]
    private bool _isSplit;

    /// <summary>Side by side when true, one above the other when false.</summary>
    [ObservableProperty]
    private bool _splitVertical = true;

    [ObservableProperty]
    private EditorGroup _focusedGroup = null!;

    /// <summary>Every open document once, first group first.</summary>
    public IReadOnlyList<DocumentTabViewModel> OpenTabs => [.. Primary.Tabs.Union(Secondary.Tabs)];

    public bool IsOpen(DocumentTabViewModel tab) => Primary.Tabs.Contains(tab) || Secondary.Tabs.Contains(tab);

    public EditorGroup OtherGroup(EditorGroup group) => group == Primary ? Secondary : Primary;

    /// <summary>The group showing a tab, preferring the focused one; null when it isn't open.</summary>
    public EditorGroup? GroupOf(DocumentTabViewModel tab) =>
        FocusedGroup.Tabs.Contains(tab) ? FocusedGroup : OtherGroup(FocusedGroup).Tabs.Contains(tab) ? OtherGroup(FocusedGroup) : null;

    private void InitializeGroups()
    {
        FocusedGroup = Primary;
        foreach (var group in new[] { Primary, Secondary })
        {
            group.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(EditorGroup.ActiveTab) && group == FocusedGroup)
                {
                    ActiveTab = group.ActiveTab;
                }
            };
            group.Tabs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTabs));
        }
    }

    partial void OnFocusedGroupChanged(EditorGroup? oldValue, EditorGroup newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsFocused = false;
        }

        newValue.IsFocused = true;
        ActiveTab = newValue.ActiveTab;
    }

    /// <summary>Keeps the focused group in step when code activates a tab (it may live in the other group).</summary>
    private void FollowActiveTab(DocumentTabViewModel? tab)
    {
        if (tab is null)
        {
            return;
        }

        if (!FocusedGroup.Tabs.Contains(tab) && OtherGroup(FocusedGroup).Tabs.Contains(tab))
        {
            OtherGroup(FocusedGroup).ActiveTab = tab;
            FocusedGroup = OtherGroup(FocusedGroup);
        }
        else if (FocusedGroup.Tabs.Contains(tab))
        {
            FocusedGroup.ActiveTab = tab;
        }
    }

    [RelayCommand]
    private void SplitRight() => Split(vertical: true);

    [RelayCommand]
    private void SplitDown() => Split(vertical: false);

    /// <summary>Opens the second editor with the current document in it (a second view of the same text).</summary>
    private void Split(bool vertical)
    {
        SplitVertical = vertical;
        if (IsSplit)
        {
            return;
        }

        CaptureEditorState?.Invoke();
        IsSplit = true;
        if (ActiveTab is { } tab)
        {
            AddToGroup(Secondary, tab);
            Secondary.ActiveTab = tab;
        }

        FocusedGroup = Secondary;
        EditorFocusRequested?.Invoke();
    }

    /// <summary>Back to one editor: the second group's documents join the first.</summary>
    [RelayCommand]
    private void Unsplit()
    {
        if (!IsSplit)
        {
            return;
        }

        CaptureEditorState?.Invoke();
        var keep = FocusedGroup.ActiveTab;
        foreach (var tab in Secondary.Tabs.ToList())
        {
            AddToGroup(Primary, tab);
            Secondary.Forget(tab);
        }

        Secondary.Tabs.Clear();
        Secondary.ActiveTab = null;
        IsSplit = false;
        FocusedGroup = Primary;
        Primary.ActiveTab = keep ?? Primary.ActiveTab;
    }

    [RelayCommand]
    private void FocusOtherGroup()
    {
        if (IsSplit)
        {
            FocusedGroup = OtherGroup(FocusedGroup);
            EditorFocusRequested?.Invoke();
        }
    }

    /// <summary>Shows a tab in the other group too (splitting first if needed).</summary>
    [RelayCommand]
    private void OpenInOtherGroup(DocumentTabViewModel? tab)
    {
        if ((tab ?? ActiveTab) is not { } target)
        {
            return;
        }

        if (!IsSplit)
        {
            Split(vertical: SplitVertical);
            return;
        }

        var other = OtherGroup(GroupOf(target) ?? FocusedGroup);
        AddToGroup(other, target);
        other.ActiveTab = target;
        FocusedGroup = other;
        EditorFocusRequested?.Invoke();
    }

    /// <summary>Moves a tab to the other group (splitting first if needed).</summary>
    [RelayCommand]
    private void MoveToOtherGroup(DocumentTabViewModel? tab)
    {
        if ((tab ?? ActiveTab) is not { } target || GroupOf(target) is not { } from)
        {
            return;
        }

        IsSplit = true;

        CaptureEditorState?.Invoke();
        var to = OtherGroup(from);
        AddToGroup(to, target);
        to.ActiveTab = target;
        RemoveFromGroup(from, target);
        FocusedGroup = to;
        if (IsSplit && (Primary.Tabs.Count == 0 || Secondary.Tabs.Count == 0))
        {
            // Moving the last tab away leaves an empty side: one editor is enough.
            CollapseEmptyGroup();
        }

        EditorFocusRequested?.Invoke();
    }

    /// <summary>Adds a tab to a group after its active tab (no-op if it is already there).</summary>
    private void AddToGroup(EditorGroup group, DocumentTabViewModel tab)
    {
        if (group.Tabs.Contains(tab))
        {
            return;
        }

        var index = group.ActiveTab is null ? group.Tabs.Count : group.Tabs.IndexOf(group.ActiveTab) + 1;
        group.Adopt(tab);
        group.Tabs.Insert(Math.Clamp(index, 0, group.Tabs.Count), tab);
    }

    /// <summary>Takes a tab out of one group, choosing the neighbour as the group's new active tab.</summary>
    private void RemoveFromGroup(EditorGroup group, DocumentTabViewModel tab)
    {
        var index = group.Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        var wasActive = group.ActiveTab == tab;
        group.Tabs.RemoveAt(index);
        group.Forget(tab);
        if (wasActive || group.ActiveTab is null)
        {
            group.ActiveTab = group.Tabs.Count == 0 ? null : group.Tabs[Math.Min(index, group.Tabs.Count - 1)];
        }

        if (IsSplit && group.Tabs.Count == 0)
        {
            CollapseEmptyGroup();
        }
    }

    /// <summary>When one side has no tabs left, the window goes back to a single editor.</summary>
    private void CollapseEmptyGroup()
    {
        if (Secondary.Tabs.Count == 0)
        {
            IsSplit = false;
            FocusedGroup = Primary;
        }
        else if (Primary.Tabs.Count == 0)
        {
            // Keep the remaining documents in the first group so the layout stays "first = left/top".
            Unsplit();
        }
    }
}
