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

namespace Ultiviewer.App.Views;

/// <summary>
/// Hosts the single AvalonEdit editor. Switching tabs swaps the editor's <see cref="TextDocument"/> (each tab
/// keeps its own text and undo history) and saves/restores caret, selection and scroll position per tab.
/// </summary>
public partial class EditorHost : UserControl
{
    private MainViewModel? _vm;
    private DocumentTabViewModel? _current;
    private (int Caret, double Vertical)? _reloadState;

    public EditorHost()
    {
        InitializeComponent();
        ConfigureEditor();
        DataContextChanged += (_, e) => Attach(e.NewValue as MainViewModel);
        ThemeService.Instance.Changed += () =>
        {
            ApplyPalette();
            ApplyHighlighting();
        };
        Editor.TextArea.Caret.PositionChanged += (_, _) => UpdateCaretStatus();
        Editor.TextArea.SelectionChanged += (_, _) => UpdateCaretStatus();
        Editor.PreviewMouseWheel += OnEditorMouseWheel;
        Editor.AddHandler(Hyperlink.RequestNavigateEvent, new System.Windows.Navigation.RequestNavigateEventHandler(OnRequestNavigate));
        Editor.ContextMenu = BuildContextMenu();
        InitializePreview();
        SplitterMoved += SavePreviewRatio;
    }

    public TextEditor TextEditor => Editor;

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
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.EditorFocusRequested -= FocusEditor;
            _vm.GoToLineRequested -= GoToLine;
            _vm.TabReloading -= OnTabReloading;
            _vm.TabReloaded -= OnTabReloaded;
            _vm.CaptureEditorState -= CaptureCurrent;
            _vm.EditorSettingsChanged -= ApplySettings;
            _vm.DocumentEdited -= OnDocumentEdited;
        }

        _vm = vm;
        if (vm is null)
        {
            return;
        }

        vm.PropertyChanged += OnViewModelPropertyChanged;
        vm.EditorFocusRequested += FocusEditor;
        vm.GoToLineRequested += GoToLine;
        vm.TabReloading += OnTabReloading;
        vm.TabReloaded += OnTabReloaded;
        vm.CaptureEditorState += CaptureCurrent;
        vm.EditorSettingsChanged += ApplySettings;
        vm.DocumentEdited += OnDocumentEdited;
        ApplySettings();
        ApplyPalette();
        SwitchTo(vm.ActiveTab);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ActiveTab))
        {
            SwitchTo(_vm!.ActiveTab);
        }
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
        }
        else if (e.PropertyName is nameof(DocumentTabViewModel.ViewMode) or nameof(DocumentTabViewModel.Path))
        {
            ApplyViewMode();
        }
    }

    private void RestoreView(DocumentTabViewModel tab)
    {
        if (_current != tab)
        {
            return;
        }

        var length = tab.Document.TextLength;
        var start = Math.Clamp(tab.SelectionStart, 0, length);
        Editor.Select(start, Math.Clamp(tab.SelectionLength, 0, length - start));
        Editor.TextArea.Caret.Offset = Math.Clamp(tab.CaretOffset, 0, length);
        if (tab.PendingLine is { } line)
        {
            tab.PendingLine = null;
            GoToLine(line);
        }
        else
        {
            Editor.ScrollToVerticalOffset(tab.VerticalOffset);
            Editor.ScrollToHorizontalOffset(tab.HorizontalOffset);
        }
    }

    private void CaptureCurrent()
    {
        if (_current is null)
        {
            return;
        }

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
        Dispatcher.BeginInvoke(() =>
        {
            Editor.TextArea.Focus();
            Keyboard.Focus(Editor.TextArea);
        }, DispatcherPriority.Input);
    }

    public void GoToLine(int lineNumber)
    {
        if (Editor.Document.LineCount == 0)
        {
            return;
        }

        var line = Editor.Document.GetLineByNumber(Math.Clamp(lineNumber, 1, Editor.Document.LineCount));
        Editor.Select(line.Offset, 0);
        Editor.TextArea.Caret.Offset = line.Offset;
        Editor.UpdateLayout();
        Editor.ScrollTo(line.LineNumber, 1, VisualYPosition.LineTop, Editor.TextArea.TextView.ActualHeight / 3, 0.3);
    }

    private void UpdateCaretStatus()
    {
        if (_vm is null)
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
        try
        {
            Editor.FontFamily = new FontFamily(settings.EditorFontFamily);
        }
        catch (ArgumentException)
        {
            Editor.FontFamily = new FontFamily("Consolas");
        }

        Editor.FontSize = settings.EditorFontSize;
        Editor.WordWrap = settings.WordWrap;
        Editor.ShowLineNumbers = settings.ShowLineNumbers;
        var options = Editor.Options;
        options.ShowSpaces = settings.ShowWhitespace;
        options.ShowTabs = settings.ShowWhitespace;
        options.ShowEndOfLine = settings.ShowWhitespace;
        options.HighlightCurrentLine = settings.HighlightCurrentLine;
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
        Editor.SyntaxHighlighting = _current is null
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
