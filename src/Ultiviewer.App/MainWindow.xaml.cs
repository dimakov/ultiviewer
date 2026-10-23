using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Ultiviewer.App.Editor;
using Ultiviewer.App.ViewModels;
using Ultiviewer.Core.Files;

namespace Ultiviewer.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        RestorePlacement();
        Tree.OpenRequested += node => _vm.OpenDocument(node.Path, preview: false, focus: true);
        Activated += (_, _) => _vm.CheckAllDiskStates();
        _vm.SearchFocusRequested += SearchView.FocusQuery;
        _vm.QuickOpenRequested += () => new Views.QuickOpenWindow(_vm, this).Show();
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsSearchPanelOpen))
            {
                UpdateResultsRow();
            }
        };
        ResultsSplitter.DragCompleted += (_, _) => _vm.Session.ResultsHeight = ResultsRow.ActualHeight;
        PreviewKeyDown += OnWindowPreviewKeyDown;
        Services.KeyboardShortcuts.BindApplicationCommands(this, vm, vm.Settings.Shortcuts);
        _vm.SettingsRequested += ShowSettings;
    }

    /// <summary>Brings the window forward and opens files handed over by a second launch.</summary>
    public void OpenFromAnotherLaunch(IEnumerable<string> files)
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        foreach (var file in files)
        {
            _vm.OpenDocument(file, preview: false);
        }
    }

    private void ShowSettings()
    {
        var settings = new SettingsViewModel(_vm.Settings);
        if (new Views.SettingsWindow(settings) { Owner = this }.ShowDialog() == true)
        {
            settings.ApplyTo(_vm.Settings);
            _vm.Settings.Save();
            _vm.ReloadSettingsIntoView();
        }
    }

    private void OnOpenErrorLog(object sender, RoutedEventArgs e)
    {
        if (System.IO.File.Exists(Services.ErrorLog.FilePath))
        {
            _vm.OpenDocument(Services.ErrorLog.FilePath, preview: false);
        }
        else
        {
            _vm.StatusMessage = "No errors have been logged.";
        }
    }

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        var version = typeof(MainWindow).Assembly.GetName().Version;
        MessageBox.Show(this,
            $"{Core.AppInfo.Name} {version?.ToString(3)}\n\nNotes as plain Markdown files: OneNote's notebooks, Notepad++'s editing.\n\n"
            + $"Settings: {Core.Settings.AppPaths.RoamingDirectory}\nIndex, session and backups: {Core.Settings.AppPaths.LocalDirectory}\n\n"
            + "Bundled: AvalonEdit (MIT), Markdig (BSD-2), CommunityToolkit.Mvvm (MIT), Microsoft.Data.Sqlite (MIT), WebView2 SDK, highlight.js (BSD-3), Mermaid (MIT), KaTeX (MIT).",
            "About " + Core.AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void UpdateResultsRow() =>
        ResultsRow.Height = _vm.IsSearchPanelOpen ? new GridLength(Math.Clamp(_vm.Session.ResultsHeight, 100, 800)) : new GridLength(0);

    private void OnWindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F6 && System.Windows.Input.Keyboard.Modifiers is System.Windows.Input.ModifierKeys.None or System.Windows.Input.ModifierKeys.Shift)
        {
            CyclePane(forward: System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None);
            e.Handled = true;
            return;
        }

        if (e.Key == System.Windows.Input.Key.F && System.Windows.Input.Keyboard.Modifiers == (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift))
        {
            OnFindInNotebooks(this, e);
            e.Handled = true;
        }
    }

    /// <summary>F6 / Shift+F6: move keyboard focus between the left pane, the editor and the search results.</summary>
    private void CyclePane(bool forward)
    {
        var panes = new List<(Func<bool> HasFocus, Action Focus)>
        {
            (() => LeftTabs.IsKeyboardFocusWithin, () =>
            {
                if (LeftTabs.SelectedIndex == 0)
                {
                    Tree.FocusTree();
                }
                else
                {
                    (LeftTabs.SelectedItem as TabItem)?.Focus();
                }
            }),
        };
        if (_vm.HasTabs)
        {
            panes.Add((() => EditorView.IsKeyboardFocusWithin || Tabs.IsKeyboardFocusWithin, _vm.FocusEditor));
        }

        if (_vm.IsSearchPanelOpen)
        {
            panes.Add((() => SearchView.IsKeyboardFocusWithin, SearchView.FocusQuery));
        }

        var current = panes.FindIndex(p => p.HasFocus());
        var next = current < 0 ? 0 : (current + (forward ? 1 : -1) + panes.Count) % panes.Count;
        panes[next].Focus();
    }

    private void OnFindInNotebooks(object sender, RoutedEventArgs e) =>
        _vm.OpenSearchCommand.Execute(_vm.HasTabs ? EditorView.TextEditor.SelectedText : null);

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel)
        {
            return;
        }

        SavePlacement();
        if (_vm.IsSearchPanelOpen)
        {
            _vm.Session.ResultsHeight = ResultsRow.ActualHeight;
        }

        _vm.SaveState();
    }

    private void RestorePlacement()
    {
        var placement = _vm.Session.Window;
        Width = Math.Max(MinWidth, placement.Width);
        Height = Math.Max(MinHeight, placement.Height);
        var onScreen = !double.IsNaN(placement.Left) && !double.IsNaN(placement.Top)
            && placement.Left >= SystemParameters.VirtualScreenLeft - 50
            && placement.Top >= SystemParameters.VirtualScreenTop - 50
            && placement.Left + 100 <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
            && placement.Top + 100 <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
        if (onScreen)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = placement.Left;
            Top = placement.Top;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (placement.Maximized)
        {
            WindowState = WindowState.Maximized;
        }

        TreeColumn.Width = new GridLength(Math.Clamp(_vm.Session.TreeWidth, 160, 800));
    }

    private void SavePlacement()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        var placement = _vm.Session.Window;
        placement.Left = bounds.Left;
        placement.Top = bounds.Top;
        placement.Width = bounds.Width;
        placement.Height = bounds.Height;
        placement.Maximized = WindowState == WindowState.Maximized;
        _vm.Session.TreeWidth = TreeColumn.ActualWidth;
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (!e.Handled && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (var file in files.Where(System.IO.File.Exists))
            {
                _vm.OpenDocument(file, preview: false);
            }

            e.Handled = true;
        }
    }

    // ---- Status bar pickers ----

    private void OnEncodingClick(object sender, RoutedEventArgs e)
    {
        if (_vm.ActiveTab is not { } tab)
        {
            return;
        }

        var menu = new ContextMenu();
        var convert = new MenuItem { Header = "Save with encoding" };
        foreach (var (name, encoding, bom) in EncodingChoices())
        {
            var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = tab.Format.Encoding.CodePage == encoding.CodePage && tab.Format.HasBom == bom };
            item.Click += (_, _) => _vm.SetEncoding(tab, encoding, bom);
            convert.Items.Add(item);
        }

        menu.Items.Add(convert);
        if (tab.Path is not null)
        {
            var reopen = new MenuItem { Header = "Reopen with encoding" };
            foreach (var (name, encoding, bom) in EncodingChoices().Where(c => !c.Bom || c.Encoding.CodePage != 65001))
            {
                var item = new MenuItem { Header = name.Replace(" BOM", "") };
                item.Click += (_, _) => _vm.ReopenWithEncoding(tab, encoding);
                reopen.Items.Add(item);
            }

            menu.Items.Add(reopen);
        }

        ShowMenu(menu, (Button)sender);
    }

    private static IEnumerable<(string Name, Encoding Encoding, bool Bom)> EncodingChoices()
    {
        yield return ("UTF-8", TextFileFormat.Utf8, false);
        yield return ("UTF-8 BOM", TextFileFormat.Utf8, true);
        yield return ("UTF-16 LE BOM", new UnicodeEncoding(false, false), true);
        yield return ("UTF-16 BE BOM", new UnicodeEncoding(true, false), true);
        var ansi = TextFileFormat.Ansi;
        yield return (new TextFileFormat(ansi, false, LineEnding.CrLf).EncodingName + " (ANSI)", ansi, false);
    }

    private void OnLineEndingClick(object sender, RoutedEventArgs e)
    {
        if (_vm.ActiveTab is not { } tab)
        {
            return;
        }

        var menu = new ContextMenu();
        foreach (var (name, ending) in new[] { ("Windows (CRLF)", LineEnding.CrLf), ("Unix (LF)", LineEnding.Lf), ("Classic Mac (CR)", LineEnding.Cr) })
        {
            var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = tab.Format.LineEnding == ending };
            item.Click += (_, _) => _vm.SetLineEnding(tab, ending);
            menu.Items.Add(item);
        }

        ShowMenu(menu, (Button)sender);
    }

    private void OnLanguageClick(object sender, RoutedEventArgs e)
    {
        if (_vm.ActiveTab is not { } tab)
        {
            return;
        }

        var menu = new ContextMenu();
        foreach (var name in HighlightingRegistry.Instance.Names)
        {
            var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = tab.Language == name };
            item.Click += (_, _) => _vm.SetLanguage(tab, name);
            menu.Items.Add(item);
        }

        ShowMenu(menu, (Button)sender);
    }

    // ---- Go and template menus (built when opened) ----

    private void OnGoMenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, GoMenu))
        {
            return;
        }

        GoMenu.Items.Clear();
        GoMenu.Items.Add(new MenuItem { Header = "Go to _page…", Command = _vm.QuickOpenCommand, InputGestureText = "Ctrl+E" });
        GoMenu.Items.Add(new MenuItem { Header = "Find in notebooks…", InputGestureText = "Ctrl+Shift+F" });
        ((MenuItem)GoMenu.Items[1]).Click += OnFindInNotebooks;
        var tags = new MenuItem { Header = "Show _tags" };
        tags.Click += (_, _) => LeftTabs.SelectedIndex = 1;
        var todo = new MenuItem { Header = "Show _to-do list" };
        todo.Click += (_, _) => LeftTabs.SelectedIndex = 2;
        GoMenu.Items.Add(tags);
        GoMenu.Items.Add(todo);
        GoMenu.Items.Add(new Separator());

        if (_vm.ActiveTab?.Path is { } current)
        {
            var toggle = new MenuItem { Header = _vm.IsFavorite(current) ? "Remove current page from _favorites" : "Add current page to _favorites" };
            toggle.Click += (_, _) => _vm.ToggleFavorite(current);
            GoMenu.Items.Add(toggle);
        }

        var favorites = new MenuItem { Header = "F_avorites" };
        foreach (var path in _vm.Session.FavoritePages.Where(System.IO.File.Exists))
        {
            favorites.Items.Add(PageItem(path));
        }

        favorites.IsEnabled = favorites.Items.Count > 0;
        GoMenu.Items.Add(favorites);

        var recent = new MenuItem { Header = "_Recent pages" };
        foreach (var path in _vm.Session.RecentPages.Where(System.IO.File.Exists).Take(15))
        {
            recent.Items.Add(PageItem(path));
        }

        recent.IsEnabled = recent.Items.Count > 0;
        GoMenu.Items.Add(recent);
    }

    private MenuItem PageItem(string path)
    {
        var item = new MenuItem
        {
            Header = System.IO.Path.GetFileNameWithoutExtension(path).Replace("_", "__"),
            ToolTip = path,
        };
        item.Click += (_, _) => _vm.OpenDocument(path, preview: false);
        return item;
    }

    private void OnTemplatesMenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, TemplatesMenu))
        {
            return;
        }

        TemplatesMenu.Items.Clear();
        foreach (var template in _vm.TemplatesForCurrentNotebook())
        {
            var item = new MenuItem { Header = System.IO.Path.GetFileNameWithoutExtension(template).Replace("_", "__") };
            item.Click += (_, _) => _vm.NewPageFromTemplate(template);
            TemplatesMenu.Items.Add(item);
        }

        if (TemplatesMenu.Items.Count > 0)
        {
            TemplatesMenu.Items.Add(new Separator());
        }

        var edit = new MenuItem { Header = "Edit templates…" };
        edit.Click += (_, _) => _vm.OpenTemplatesFolder();
        TemplatesMenu.Items.Add(edit);
    }

    private static void ShowMenu(ContextMenu menu, FrameworkElement target)
    {
        menu.PlacementTarget = target;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }
}
