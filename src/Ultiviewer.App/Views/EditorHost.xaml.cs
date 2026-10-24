using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Ultiviewer.App.Editor;
using Ultiviewer.App.Services;
using Ultiviewer.App.ViewModels;
using Ultiviewer.Core.Settings;

namespace Ultiviewer.App.Views;

/// <summary>
/// Hosts one editor pane's AvalonEdit editor, showing its <see cref="Group"/>'s active tab. Switching tabs swaps
/// the editor's <see cref="TextDocument"/> (each document keeps its own text and undo history) and saves/restores
/// caret, selection and scroll position per tab and per editor, so a document open in both editors keeps two
/// positions. Requests aimed at "the editor" (focus, go to line, select) only reach the focused group's host.
/// </summary>
public partial class EditorHost : UserControl
{
    private MainViewModel? _vm;
    private EditorGroup? _group;
    private DocumentTabViewModel? _current;
    private (int Caret, double Vertical)? _reloadState;
    private readonly Dictionary<DocumentTabViewModel, ViewState> _viewStates = [];

    private readonly record struct ViewState(int Caret, int SelectionStart, int SelectionLength, double Vertical, double Horizontal);

    public EditorHost()
    {
        InitializeComponent();
        ConfigureEditor();
        DataContextChanged += (_, e) => Attach(e.NewValue as MainViewModel);
        ThemeService.Instance.Changed += () =>
        {
            ApplyPalette();
            ApplyHighlighting();
            _live?.SetStyles(CreateLiveStyles());
        };
        Editor.TextArea.Caret.PositionChanged += (_, _) => UpdateCaretStatus();
        Editor.TextArea.SelectionChanged += (_, _) => UpdateCaretStatus();
        Editor.PreviewMouseWheel += OnEditorMouseWheel;
        Editor.AddHandler(Hyperlink.RequestNavigateEvent, new System.Windows.Navigation.RequestNavigateEventHandler(OnRequestNavigate));
        Editor.ContextMenu = BuildContextMenu();
        PreviewMouseDown += (_, _) => ClaimFocus();
        IsKeyboardFocusWithinChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                ClaimFocus();
            }
        };
        InitializePreview();
        InitializeCommands();
        InitializeConveniences();
        InitializeFormatting();
        InitializeLive();
        InitializeTables();
        SplitterMoved += SavePreviewRatio;
    }

    public TextEditor TextEditor => Editor;

    /// <summary>The tabs this editor shows; set by the pane that contains it.</summary>
    public EditorGroup? Group
    {
        get => _group;
        set
        {
            if (_group == value)
            {
                return;
            }

            if (_group is not null)
            {
                _group.PropertyChanged -= OnGroupPropertyChanged;
                _group.ModeChanged -= OnGroupModeChanged;
                _group.Tabs.CollectionChanged -= OnGroupTabsChanged;
            }

            _group = value;
            if (value is not null)
            {
                value.PropertyChanged += OnGroupPropertyChanged;
                value.ModeChanged += OnGroupModeChanged;
                value.Tabs.CollectionChanged += OnGroupTabsChanged;
            }

            SwitchTo(value?.ActiveTab);
        }
    }

    private bool IsFocusedGroup => _vm is not null && _group is not null && _vm.FocusedGroup == _group;

    private void ClaimFocus()
    {
        if (_vm is not null && _group is not null && _vm.FocusedGroup != _group)
        {
            _vm.FocusedGroup = _group;
        }
    }

    private void OnGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorGroup.ActiveTab))
        {
            SwitchTo(_group!.ActiveTab);
        }
    }

    private void OnGroupModeChanged(DocumentTabViewModel tab)
    {
        if (tab == _current)
        {
            ApplyViewMode();
            UpdateFormatBar();
        }
    }

    private void OnGroupTabsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        foreach (var gone in _viewStates.Keys.Where(t => _group is null || !_group.Tabs.Contains(t)).ToList())
        {
            _viewStates.Remove(gone);
        }
    }

    public DocumentTabViewModel? CurrentTab => _current;

    /// <summary>Raised after a different tab (or none) is shown in the editor.</summary>
    public event Action<DocumentTabViewModel?>? TabShown;

    private void ConfigureEditor()
    {
        var options = Editor.Options;
        options.EnableRectangularSelection = true;
        options.CutCopyWholeLine = true;
        options.AllowScrollBelowDocument = true;
        options.EnableHyperlinks = true;
        options.RequireControlModifierForHyperlinkClick = true;
        options.EnableEmailHyperlinks = false;
        options.ConvertTabsToSpaces = false;
        options.IndentationSize = 4;
        options.ShowBoxForControlCharacters = true;
        options.EnableTextDragDrop = true;
        Editor.TextArea.TextView.LinkTextUnderline = true;
    }

    private void Attach(MainViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.EditorFocusRequested -= FocusEditor;
            _vm.GoToLineRequested -= GoToLine;
            _vm.TabReloading -= OnTabReloading;
            _vm.TabReloaded -= OnTabReloaded;
            _vm.CaptureEditorState -= CaptureCurrent;
            _vm.EditorSettingsChanged -= ApplySettings;
            _vm.DocumentEdited -= OnDocumentEdited;
            _vm.SelectRangeRequested -= SelectRange;
        }

        _vm = vm;
        if (vm is null)
        {
            return;
        }

        vm.EditorFocusRequested += FocusEditor;
        vm.GoToLineRequested += GoToLine;
        vm.TabReloading += OnTabReloading;
        vm.TabReloaded += OnTabReloaded;
        vm.CaptureEditorState += CaptureCurrent;
        vm.EditorSettingsChanged += ApplySettings;
        vm.DocumentEdited += OnDocumentEdited;
        vm.SelectRangeRequested += SelectRange;
        ApplySettings();
        ApplyPalette();
        SwitchTo(_group?.ActiveTab);
    }

    private void SwitchTo(DocumentTabViewModel? tab)
    {
        if (_current == tab)
        {
            return;
        }

        if (_current is not null)
        {
            CaptureCurrent();
            _current.PropertyChanged -= OnTabPropertyChanged;
        }

        _current = tab;
        if (tab is null)
        {
            Editor.Document = new TextDocument();
            Editor.SyntaxHighlighting = null;
            ApplyViewMode();
            TabShown?.Invoke(null);
            return;
        }

        tab.PropertyChanged += OnTabPropertyChanged;
        Editor.Document = tab.Document;
        ApplyHighlighting();
        UpdateCaretStatus();
        ApplyViewMode();
        TabShown?.Invoke(tab);
        Dispatcher.BeginInvoke(() => RestoreView(tab), DispatcherPriority.Loaded);
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentTabViewModel.Language))
        {
            ApplyHighlighting();
            ApplyViewMode();
            UpdateFormatBar();
        }
        else if (e.PropertyName is nameof(DocumentTabViewModel.ViewMode) or nameof(DocumentTabViewModel.Path))
        {
            ApplyViewMode();
            UpdateFormatBar();
        }
    }

    private void RestoreView(DocumentTabViewModel tab)
    {
        if (_current != tab)
        {
            return;
        }

        // This editor's own position for the tab, else the one stored in the tab (session, the other editor).
        var state = _viewStates.TryGetValue(tab, out var own)
            ? own
            : new ViewState(tab.CaretOffset, tab.SelectionStart, tab.SelectionLength, tab.VerticalOffset, tab.HorizontalOffset);
        var length = tab.Document.TextLength;
        var start = Math.Clamp(state.SelectionStart, 0, length);
        Editor.Select(start, Math.Clamp(state.SelectionLength, 0, length - start));
        Editor.TextArea.Caret.Offset = Math.Clamp(state.Caret, 0, length);
        if (tab.PendingLine is { } line && IsFocusedGroup)
        {
            tab.PendingLine = null;
            GoToLine(line);
        }
        else
        {
            Editor.ScrollToVerticalOffset(state.Vertical);
            Editor.ScrollToHorizontalOffset(state.Horizontal);
        }
    }

    private void CaptureCurrent()
    {
        if (_current is null)
        {
            return;
        }

        _viewStates[_current] = new ViewState(Editor.CaretOffset, Editor.SelectionStart, Editor.SelectionLength, Editor.VerticalOffset, Editor.HorizontalOffset);
        _current.CaretOffset = Editor.CaretOffset;
        _current.SelectionStart = Editor.SelectionStart;
        _current.SelectionLength = Editor.SelectionLength;
        _current.VerticalOffset = Editor.VerticalOffset;
        _current.HorizontalOffset = Editor.HorizontalOffset;
    }

    private void OnTabReloading(DocumentTabViewModel tab)
    {
        if (tab == _current)
        {
            _reloadState = (Editor.CaretOffset, Editor.VerticalOffset);
        }
    }

    private void OnTabReloaded(DocumentTabViewModel tab)
    {
        if (tab == _current && _reloadState is { } state)
        {
            _reloadState = null;
            Editor.TextArea.Caret.Offset = Math.Min(state.Caret, Editor.Document.TextLength);
            Dispatcher.BeginInvoke(() => Editor.ScrollToVerticalOffset(state.Vertical), DispatcherPriority.Loaded);
        }
    }

    public void FocusEditor()
    {
        if (!IsFocusedGroup)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            Editor.TextArea.Focus();
            Keyboard.Focus(Editor.TextArea);
        }, DispatcherPriority.Input);
    }

    public void GoToLine(int lineNumber)
    {
        if (!IsFocusedGroup || Editor.Document.LineCount == 0)
        {
            return;
        }

        var line = Editor.Document.GetLineByNumber(Math.Clamp(lineNumber, 1, Editor.Document.LineCount));
        Editor.Select(line.Offset, 0);
        Editor.TextArea.Caret.Offset = line.Offset;
        Editor.UpdateLayout();
        Editor.ScrollTo(line.LineNumber, 1, VisualYPosition.LineTop, Editor.TextArea.TextView.ActualHeight / 3, 0.3);
    }

    /// <summary>Selects text on a 1-based line (search results, find bar) and scrolls it into view.</summary>
    public void SelectRange(int lineNumber, int column, int length)
    {
        var document = Editor.Document;
        if (!IsFocusedGroup || lineNumber < 1 || lineNumber > document.LineCount)
        {
            return;
        }

        var line = document.GetLineByNumber(lineNumber);
        var start = line.Offset + Math.Clamp(column, 0, line.Length);
        Editor.Select(start, Math.Clamp(length, 0, line.EndOffset - start));
        Editor.TextArea.Caret.Offset = start;
        Editor.ScrollTo(lineNumber, column + 1, VisualYPosition.LineTop, Editor.TextArea.TextView.ActualHeight / 3, 0.3);
    }

    private void UpdateCaretStatus()
    {
        if (_vm is null || !IsFocusedGroup)
        {
            return;
        }

        _vm.CaretLine = Editor.TextArea.Caret.Line;
        _vm.CaretColumn = Editor.TextArea.Caret.Column;
        _vm.SelectionLength = Editor.SelectionLength;
    }

    private void ApplySettings()
    {
        if (_vm is null)
        {
            return;
        }

        var settings = _vm.Settings;
        Editor.FontSize = settings.EditorFontSize;
        var options = Editor.Options;
        options.ShowSpaces = settings.ShowWhitespace;
        options.ShowTabs = settings.ShowWhitespace;
        options.ShowEndOfLine = settings.ShowWhitespace;
        _live?.SetStyles(CreateLiveStyles());
        ApplyEditorAppearance();
        UpdateFormatBar();
    }

    private void ApplyPalette()
    {
        var palette = ThemeService.Instance.Palette;
        Editor.Background = Brush(palette.Background);
        Editor.Foreground = Brush(palette.Foreground);
        Editor.LineNumbersForeground = Brush(palette.LineNumbers);
        var area = Editor.TextArea;
        area.TextView.CurrentLineBackground = Brush(palette.CurrentLine);
        area.TextView.CurrentLineBorder = new Pen(Brushes.Transparent, 0);
        area.SelectionBrush = Brush(palette.Selection);
        area.SelectionForeground = null;
        area.SelectionBorder = null;
        area.SelectionCornerRadius = 0;
        area.Caret.CaretBrush = Brush(palette.Foreground);
        area.TextView.LinkTextForegroundBrush = Brush(ThemeService.Instance.IsDark ? Color.FromRgb(0x6C, 0xB6, 0xFF) : Color.FromRgb(0x0B, 0x66, 0xC2));
    }

    private void ApplyHighlighting() =>
        Editor.SyntaxHighlighting = _current is null || EffectiveMode == ViewMode.Live
            ? null
            : HighlightingRegistry.Instance.Get(_current.Language, ThemeService.Instance.IsDark);

    private void OnEditorMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && _vm is not null)
        {
            _vm.SetFontSize(_vm.Settings.EditorFontSize + (e.Delta > 0 ? 1 : -1));
            e.Handled = true;
        }
    }

    private static void OnRequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        ShellService.Open(e.Uri.ToString());
        e.Handled = true;
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Command = ApplicationCommands.Cut, CommandTarget = Editor.TextArea });
        menu.Items.Add(new MenuItem { Command = ApplicationCommands.Copy, CommandTarget = Editor.TextArea });
        menu.Items.Add(new MenuItem { Command = ApplicationCommands.Paste, CommandTarget = Editor.TextArea });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Command = ApplicationCommands.SelectAll, CommandTarget = Editor.TextArea });
        menu.Opened += (_, _) => ContextMenuOpened?.Invoke(menu);
        return menu;
    }

    /// <summary>Lets other features add items to the editor context menu each time it opens.</summary>
    public event Action<ContextMenu>? ContextMenuOpened;

    private void OnSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) =>
        SplitterMoved?.Invoke();

    public event Action? SplitterMoved;

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
