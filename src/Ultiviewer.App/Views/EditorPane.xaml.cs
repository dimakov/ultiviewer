using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ultiviewer.App.ViewModels;

namespace Ultiviewer.App.Views;

/// <summary>
/// One editor pane: a tab strip, the view-mode buttons and an <see cref="EditorHost"/>, all showing one
/// <see cref="EditorGroup"/>. The main window has two; the focused one (when split) gets an accent line on top.
/// </summary>
public partial class EditorPane : UserControl
{
    public static readonly DependencyProperty GroupProperty = DependencyProperty.Register(
        nameof(Group), typeof(EditorGroup), typeof(EditorPane), new PropertyMetadata(null, (d, e) => ((EditorPane)d).OnGroupChanged((EditorGroup?)e.OldValue)));

    private MainViewModel? _vm;

    public EditorPane()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnViewModelPropertyChanged;
            }

            _vm = e.NewValue as MainViewModel;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnViewModelPropertyChanged;
                Strip.Vm = _vm;
            }

            UpdateFrame();
        };

        // Clicking anywhere in the pane (tabs, mode buttons, editor, preview) makes it the focused editor first.
        PreviewMouseDown += (_, _) =>
        {
            if (_vm is not null && Group is not null)
            {
                _vm.FocusedGroup = Group;
            }
        };
    }

    public EditorGroup? Group
    {
        get => (EditorGroup?)GetValue(GroupProperty);
        set => SetValue(GroupProperty, value);
    }

    public EditorHost Host => HostView;

    private void OnGroupChanged(EditorGroup? old)
    {
        if (old is not null)
        {
            old.PropertyChanged -= OnGroupPropertyChanged;
        }

        if (Group is { } group)
        {
            group.PropertyChanged += OnGroupPropertyChanged;
        }

        Strip.DataContext = Group;
        HostView.Group = Group;
        UpdateFrame();
    }

    private void OnGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorGroup.IsFocused))
        {
            UpdateFrame();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsSplit))
        {
            UpdateFrame();
        }
    }

    private void UpdateFrame() =>
        Frame.BorderBrush = _vm is { IsSplit: true } && Group is { IsFocused: true }
            ? (Brush)FindResource(SystemColors.HighlightBrushKey)
            : Brushes.Transparent;
}
