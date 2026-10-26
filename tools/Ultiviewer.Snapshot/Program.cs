using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using Ultiviewer.App;
using Ultiviewer.App.Services;
using Ultiviewer.App.ViewModels;
using Ultiviewer.Core.Notebooks;
using Ultiviewer.Core.Settings;

namespace Ultiviewer.Snapshot;

/// <summary>
/// Developer tool: runs Ultiviewer's real main window off-screen without activating it, follows a JSON script
/// (open a file, switch modes, move the caret, type) and saves PNG snapshots. UI changes can be checked this
/// way while someone else uses the desktop: nothing takes focus and no keystrokes go to other windows.
/// Usage: <c>Ultiviewer.Snapshot script.json</c>.
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: Ultiviewer.Snapshot script.json");
            return 2;
        }

        var script = JsonSerializer.Deserialize<Script>(File.ReadAllText(args[0]), JsonOptions)!;
        Directory.CreateDirectory(script.Home);
        Environment.SetEnvironmentVariable("ULTIVIEWER_HOME", script.Home);

        // A plain Application with the app's resources: Ultiviewer's App class would run its real startup
        // (single instance, a visible main window) as soon as the dispatcher runs.
        // Awaits in the app resume on this (UI) thread, as they do when the app runs normally.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Ultiviewer;component/AppResources.xaml") });
        app.DispatcherUnhandledException += (_, e) =>
        {
            Console.Error.WriteLine(e.Exception);
            Environment.Exit(3);
        };
        var settings = AppSettings.Load();
        ThemeService.Instance.Apply(script.Theme == "dark" ? ThemePreference.Dark : ThemePreference.Light);
        var dialogs = new ScriptDialogs();
        var vm = new MainViewModel(settings, SessionState.Load(), dialogs, new NotebookOperations(new RecycleBinDeleter()));
        var window = new MainWindow(vm)
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowState = WindowState.Normal,
            Left = -32000,
            Top = -32000,
            Width = script.Width,
            Height = script.Height,
        };
        window.Show();
        vm.Initialize(script.File is null ? [] : [script.File]);
        Pump(1000);

        try
        {
            foreach (var step in script.Steps)
            {
                dialogs.ConfirmAnswer = step.Confirm ?? dialogs.ConfirmAnswer;
                dialogs.PromptAnswer = step.Prompt ?? dialogs.PromptAnswer;
                Run(step, vm, window);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            Environment.Exit(1);
        }

        Environment.Exit(0);
        return 0;
    }

    private static void Run(Step step, MainViewModel vm, MainWindow window)
    {
        if (step.Split is { } split)
        {
            (split switch
            {
                "right" => vm.SplitRightCommand,
                "down" => vm.SplitDownCommand,
                _ => vm.UnsplitCommand,
            }).Execute(null);
            Pump(400);
        }

        if (step.FocusGroup is { } focus)
        {
            vm.FocusedGroup = focus == 0 ? vm.Primary : vm.Secondary;
        }

        if (step.Open is { } open)
        {
            vm.OpenDocument(open, preview: false);
            Pump(500);
        }

        if (step.MoveToOther)
        {
            vm.MoveToOtherGroupCommand.Execute(null);
            Pump(300);
        }

        var editor = window.ActiveEditorHost.TextEditor;

        if (step.Theme is { } theme)
        {
            ThemeService.Instance.Apply(theme == "dark" ? ThemePreference.Dark : ThemePreference.Light);
        }

        if (step.Mode is { } mode && vm.ActiveTab is { } tab)
        {
            vm.SetTabViewMode(tab, Enum.Parse<ViewMode>(mode, ignoreCase: true));
        }

        if (step.CaretAt is { } text)
        {
            var index = editor.Document.Text.IndexOf(text, StringComparison.Ordinal);
            if (index < 0)
            {
                throw new InvalidOperationException($"text not found: {text}");
            }

            editor.TextArea.ClearSelection();
            editor.CaretOffset = index + step.CaretDelta;
        }

        if (step.Caret is { } caret)
        {
            editor.TextArea.ClearSelection();
            editor.CaretOffset = Math.Clamp(caret, 0, editor.Document.TextLength);
        }

        if (step.Select is [var start, var length])
        {
            editor.Select(start, length);
        }

        if (step.Type is { } typed)
        {
            editor.TextArea.PerformTextInput(typed);
        }

        if (step.Command is { } commandName)
        {
            var command = typeof(Ultiviewer.App.Editor.EditorCommands).GetProperty(commandName)?.GetValue(null) as RoutedUICommand
                ?? throw new InvalidOperationException($"no editor command {commandName}");
            command.Execute(null, editor.TextArea);
        }

        if (step.LeftTab is { } leftTab)
        {
            window.LeftTabs.SelectedIndex = leftTab;
        }

        if (step.Key is { } keyName)
        {
            PressKey(editor, Enum.Parse<Key>(keyName, ignoreCase: true));
        }

        if (step.Save && vm.ActiveTab is { } saving)
        {
            vm.SaveTab(saving, interactive: false);
        }

        if (step.External is { } external && vm.ActiveTab?.Path is { } externalPath)
        {
            File.WriteAllText(externalPath, external);
            File.SetLastWriteTimeUtc(externalPath, DateTime.UtcNow.AddSeconds(1));
            vm.CheckAllDiskStates();
        }

        if (step.Today)
        {
            vm.OpenTodayCommand.Execute(null);
            Pump(300);
        }

        if (step.Capture is { } capture)
        {
            Console.WriteLine("captured to " + vm.Capture(capture, step.CaptureTarget));
            Pump(300);
        }

        if (step.LinkMentions)
        {
            foreach (var mention in vm.UnlinkedMentions.ToList())
            {
                vm.LinkMention(mention);
            }

            Pump(1500);
        }

        if (step.Search is { } query)
        {
            vm.IsSearchPanelOpen = true;
            vm.SearchText = query;
            Pump(1500);
            foreach (var page in vm.SearchResults)
            {
                Console.WriteLine($"  {page.Title}{(page.IsAttachment ? " (attachment)" : "")}: " + string.Join(" | ", page.Lines.Select(l => $"{l.LineLabel} {string.Concat(l.Segments.Select(s => s.IsMatch ? "[" + s.Text + "]" : s.Text))}")));
            }
        }

        if (step.MarksAtCaret is { } marksAtCaret)
        {
            vm.ShowMarksAtCaret = marksAtCaret;
            Pump(300);
        }

        if (step.Direction is { } direction)
        {
            vm.SetDirection(vm.ActiveTab, direction switch
            {
                "rtl" => Ultiviewer.Core.Text.TextDirectionMode.RightToLeft,
                "ltr" => Ultiviewer.Core.Text.TextDirectionMode.LeftToRight,
                _ => Ultiviewer.Core.Text.TextDirectionMode.Auto,
            });
            Pump(300);
        }

        if (step.PasteHtml is { } pasteHtml)
        {
            Console.WriteLine("pasted as markdown: " + window.ActiveEditorHost.PasteHtml(pasteHtml) + " · " + vm.StatusMessage);
            Pump(300);
        }

        if (step.Clip is { } clipAddress)
        {
            var clipping = vm.ClipWebPageCommand.ExecuteAsync(clipAddress);
            Wait(clipping, 60000);
            Pump(500);
            Console.WriteLine($"clip: {vm.StatusMessage} · active={vm.ActiveTab?.Path}");
        }

        if (step.Table is { } table)
        {
            vm.SearchAsTable = table;
        }

        if (step.SaveSearch)
        {
            vm.SaveSearchCommand.Execute(null);
        }

        if (step.RunSaved is { } savedName)
        {
            vm.RunSavedSearchCommand.Execute(vm.SavedSearches.Single(s => s.Name == savedName));
            Pump(1500);
            Console.WriteLine($"ran saved search '{savedName}': {vm.SearchText} [{vm.SearchScope}] table={vm.SearchAsTable} · {vm.SearchStatus}");
        }

        if (step.SortBy is { } sortKey)
        {
            window.SearchView.SortBy(sortKey);
            Pump(200);
        }

        if (step.Export is { } export)
        {
            var request = vm.Primary.ActiveTab is null ? null : new ExportRequest(vm.ActiveTab!.Title,
                [new Ultiviewer.Core.Markdown.ExportPage(vm.ActiveTab.Title, vm.ActiveTab.Document.Text, Path.GetDirectoryName(vm.ActiveTab.Path))], false, false);
            var exporter = new Ultiviewer.App.Views.ExportWindow(request!)
            {
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
            };
            exporter.Show();
            var html = exporter.SaveHtmlAsync(export + ".html");
            try
            {
                Wait(html, 30000);
            }
            catch (TimeoutException)
            {
                Console.WriteLine("export status: " + exporter.Status.Text);
                throw;
            }

            var pdf = exporter.SavePdfAsync(export + ".pdf");
            Wait(pdf, 30000);
            Console.WriteLine($"exported {export}.html and .pdf (pdf ok: {pdf.Result})");
            exporter.Close();
        }

        if (step.ReminderWindow is { } reminderShot)
        {
            var task = vm.Tasks.FirstOrDefault(t => t.Info.Reminder is not null) ?? throw new InvalidOperationException("no task with a reminder");
            var card = new Ultiviewer.App.Views.ReminderWindow(task, vm) { Topmost = false, Left = -32000, Top = -32000 };
            card.Show();
            Pump(300);
            Save(card, reminderShot);
            Console.WriteLine($"saved {reminderShot}");
            card.Close();
        }

        if (step.CaptureWindow is { } captureShot)
        {
            var quick = new Ultiviewer.App.Views.QuickCaptureWindow(vm)
            {
                ShowActivated = false,
                ShowInTaskbar = false,
                Topmost = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
            };
            quick.Show();
            Pump(300);
            Save(quick, captureShot);
            Console.WriteLine($"saved {captureShot}");
            quick.Close();
        }

        if (step.OneNote is { } oneNote)
        {
            ImportOneNote(vm, oneNote);
        }

        if (step.Shortcuts is { } shortcuts)
        {
            ChangeShortcuts(vm, window, shortcuts, step.ShortcutsShot);
        }

        if (step.Palette is { } palette)
        {
            ShowPalette(vm, window, palette);
        }

        if (step.History is { } history && vm.ActiveTab is { } historyTab && vm.History is { } store)
        {
            ShowHistory(vm, historyTab, store, history);
        }

        if (step.Click is { } click)
        {
            Click(editor, click, step.ClickIndex);
        }

        if (step.Scroll is { } scroll)
        {
            editor.ScrollToVerticalOffset(scroll);
        }

        Pump(step.Wait ?? 250);

        if (step.Shot is { } shot)
        {
            Save(window, shot);
            Console.WriteLine($"saved {shot}");
        }

        if (step.Dump is { } dump)
        {
            File.WriteAllText(dump, editor.Document.Text);
            Console.WriteLine($"dumped {dump}");
        }

        if (step.SaveState)
        {
            try
            {
                vm.SaveState();
                Console.WriteLine("session saved");
            }
            catch (ArgumentException ex)
            {
                Console.WriteLine("save failed: " + ex.Message);
                Console.WriteLine(JsonSerializer.Serialize(vm.Session, new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
            }
        }

        if (step.Print is { } expression)
        {
            Console.WriteLine(expression switch
            {
                "caret" => $"caret {editor.CaretOffset} line {editor.TextArea.Caret.Line} col {editor.TextArea.Caret.Column}",
                "margin" => $"textview margin {editor.TextArea.TextView.Margin}",
                "links" => "  linked from: " + string.Join(" | ", vm.Backlinks.Select(l => $"{l.Title}:{l.Line} {l.Text}"))
                    + Environment.NewLine + "  links: " + string.Join(" | ", vm.OutgoingLinks.Select(l => $"{l.Title} ({l.Location}, {l.State})"))
                    + Environment.NewLine + "  mentions: " + string.Join(" | ", vm.UnlinkedMentions.Select(l => $"{l.Title}:{l.Line} {l.Text}")),
                "direction" => $"direction {vm.ActiveTab?.DirectionMode} rtl={vm.ActiveTab?.IsRightToLeft} editor={editor.FlowDirection} status={vm.ActiveTab?.DirectionLabel}",
                "tasks" => string.Join(Environment.NewLine, vm.Tasks.Select(t => $"  [{t.Group}] {t.Description} | {t.DueLabel} {t.Extras}")),
                "table" => "  columns: " + string.Join(", ", vm.TableColumns) + Environment.NewLine
                    + string.Join(Environment.NewLine, window.SearchView.TableRows.Select(r => "  " + r.Title + " | " + string.Join(" | ", vm.TableColumns.Select(c => r.Properties[c])) + " | " + r.MatchCount)),
                "saved" => string.Join(Environment.NewLine, vm.SavedSearches.Select(s => $"  {s.Name}: {s.Query} scope={s.ScopePath ?? "all"} table={s.ShowAsTable}"))
                    + Environment.NewLine + "  settings: " + string.Join(", ", vm.Settings.SavedSearches.Select(s => s.Name)),
                "groups" => $"split={vm.IsSplit} vertical={vm.SplitVertical} focused={vm.FocusedGroup.Index} "
                    + $"first=[{string.Join(", ", vm.Primary.Tabs.Select(t => (t == vm.Primary.ActiveTab ? "*" : "") + t.Title + ":" + vm.Primary.ModeOf(t)))}] "
                    + $"second=[{string.Join(", ", vm.Secondary.Tabs.Select(t => (t == vm.Secondary.ActiveTab ? "*" : "") + t.Title + ":" + vm.Secondary.ModeOf(t)))}] active={vm.ActiveTab?.Title}",
                _ => $"unknown print '{expression}'",
            });
        }
    }

    /// <summary>Raises PreviewKeyDown/KeyDown on the text area (modifier keys can't be faked this way).</summary>
    private static void PressKey(TextEditor editor, Key key)
    {
        var target = editor.TextArea;
        var source = PresentationSource.FromVisual(target)!;
        var preview = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(preview);
        if (!preview.Handled)
        {
            target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent });
        }
    }

    /// <summary>
    /// Changes shortcuts as the settings window would (name → gesture, "" removes, "default" restores), prints the
    /// conflicts, optionally renders the shortcut list, then saves and applies them to the running window.
    /// </summary>
    private static void ChangeShortcuts(MainViewModel vm, MainWindow window, Dictionary<string, string> changes, string? shot)
    {
        var settings = new SettingsViewModel(vm.Settings);
        foreach (var (name, gesture) in changes)
        {
            var row = settings.Shortcuts.Single(r => r.Name == name);
            if (gesture == "default")
            {
                settings.ResetShortcut(row);
            }
            else
            {
                settings.SetShortcut(row, gesture);
            }
        }

        foreach (var row in settings.Shortcuts.Where(r => r.HasConflict || r.HasNote))
        {
            Console.WriteLine($"  {(row.HasConflict ? "conflict" : "note")}: {row.Description} [{row.Display}] — {row.Conflict ?? row.Note}");
        }

        if (shot is not null)
        {
            var dialog = new Ultiviewer.App.Views.SettingsWindow(settings)
            {
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
            };
            dialog.Show();
            Pump(300);
            var row = settings.Shortcuts.First(r => changes.ContainsKey(r.Name));
            dialog.ShortcutList.ScrollIntoView(row);
            dialog.ShortcutList.BringIntoView();
            Pump(300);
            Save(dialog, shot);
            Console.WriteLine($"saved {shot}");
            dialog.Close();
        }

        settings.ApplyTo(vm.Settings);
        window.ApplyShortcuts();
        Console.WriteLine("  stored overrides: " + string.Join(", ", vm.Settings.Shortcuts.Select(p => $"{p.Key}={p.Value}")));
    }

    /// <summary>
    /// Opens a OneNote file in the import window off-screen (never activated), waits for its sections to be read,
    /// prints the tree, unchecks and selects items by path ("Section/Page"), saves the window, the rendered page and
    /// the Markdown tab, and optionally imports into a folder.
    /// </summary>
    private static void ImportOneNote(MainViewModel vm, OneNoteStep step)
    {
        var source = Ultiviewer.Core.OneNote.OneNoteSource.Open(step.Path);
        var model = new OneNoteImportViewModel(source, step.Path, step.Into ?? Path.GetTempPath(), new ScriptDialogs(), result =>
        {
            vm.OpenNotebook(result.Folder).IsExpanded = true;
            Console.WriteLine($"opened imported notebook {result.Folder}");
        });
        var window = new Ultiviewer.App.Views.OneNoteImportWindow(model)
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            Width = step.Width,
            Height = step.Height,
        };
        var shown = 0;
        window.PreviewShown += () => shown++;
        window.Show();
        Pump(300);
        Wait(model.Scanning, 60000);
        Pump(200);

        Ultiviewer.App.ViewModels.OneNoteImportItem Find(string path)
        {
            IEnumerable<Ultiviewer.App.ViewModels.OneNoteImportItem> items = model.Items;
            Ultiviewer.App.ViewModels.OneNoteImportItem? found = null;
            foreach (var name in path.Split('/'))
            {
                found = items.FirstOrDefault(i => i.Name == name) ?? throw new InvalidOperationException($"no item '{name}' in '{path}'");
                items = found.Children;
            }

            return found!;
        }

        foreach (var path in step.Uncheck)
        {
            Find(path).IsChecked = false;
        }

        void Print(IEnumerable<Ultiviewer.App.ViewModels.OneNoteImportItem> items, string indent)
        {
            foreach (var item in items)
            {
                var check = item.IsChecked switch { true => "[x]", false => "[ ]", null => "[-]" };
                Console.WriteLine($"{indent}{check} {item.Name}  {item.Detail}{(item.HasWarning ? "  ⚠ " + string.Join("; ", item.Notes) : "")}");
                Print(item.Children, indent + "    ");
            }
        }

        Print(model.Items, "  ");
        Console.WriteLine($"  {model.Selection} · can import: {model.CanImport}");

        if (step.Select is { } select)
        {
            var before = shown;
            Find(select).IsSelected = true;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (shown == before && watch.ElapsedMilliseconds < 30000)
            {
                Pump(100);
            }

            Pump(500);
            Console.WriteLine($"selected '{select}': {model.PageMarkdown.Length} chars of Markdown, notes: {string.Join("; ", model.Notes)}");
            if (step.PreviewShot is { } previewShot)
            {
                Wait(window.CapturePreviewAsync(previewShot), 10000);
                Console.WriteLine($"saved {previewShot}");
            }
        }

        if (step.Shot is { } shot)
        {
            Save(window, shot);
            Console.WriteLine($"saved {shot}");
        }

        if (step.MarkdownShot is { } markdownShot)
        {
            window.Views.SelectedIndex = 1;
            Pump(300);
            Save(window, markdownShot);
            Console.WriteLine($"saved {markdownShot}");
            window.Views.SelectedIndex = 0;
        }

        if (step.Into is not null && step.Import)
        {
            if (step.Name is { } name)
            {
                model.NotebookName = name;
            }

            Wait(model.ImportAsync(), 120000);
            Console.WriteLine($"import: {model.Status}");
            Console.WriteLine($"  {model.NotesHeading}");
            foreach (var note in model.Notes)
            {
                Console.WriteLine("    " + note);
            }

            if (step.ReportShot is { } reportShot)
            {
                Pump(300);
                Save(window, reportShot);
                Console.WriteLine($"saved {reportShot}");
            }
        }

        var firstSection = model.Items.SelectMany(i => i.Descendants().Prepend(i)).FirstOrDefault(i => i.Section is not null)?.Section?.Path;
        window.Close();
        var closing = System.Diagnostics.Stopwatch.StartNew();
        while (window.IsLoaded && closing.ElapsedMilliseconds < 10000)
        {
            Pump(100);
        }

        Console.WriteLine($"closed; first section file still there: {firstSection is not null && File.Exists(firstSection)}");
    }

    /// <summary>Opens the command palette off-screen (never activated), filters it, prints and optionally runs the top entry.</summary>
    private static void ShowPalette(MainViewModel vm, MainWindow window, PaletteStep step)
    {
        var palette = new Ultiviewer.App.Views.CommandPaletteWindow(window.PaletteCommands(), vm.Session.RecentCommands, window)
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            Left = -32000,
            Top = -32000,
        };
        palette.Show();
        Pump(200);
        palette.Filter(step.Query);
        Pump(200);
        Console.WriteLine($"palette '{step.Query}': {palette.Shown.Count} commands");
        foreach (var item in palette.Shown.Take(step.Count))
        {
            Console.WriteLine($"  {(item.IsChecked == true ? "✓ " : "")}{item.Key}{(item.Gesture.Length > 0 ? "  [" + item.Gesture + "]" : "")}");
        }

        if (step.Shot is { } shot)
        {
            Save(palette, shot);
            Console.WriteLine($"saved {shot}");
        }

        if (step.Run)
        {
            palette.RunSelected();
            Pump(500);
            Console.WriteLine("ran; recent: " + string.Join(" | ", vm.Session.RecentCommands));
        }
        else
        {
            palette.Close();
        }
    }

    /// <summary>Opens the history window off-screen (never activated), optionally selects, restores and snapshots.</summary>
    private static void ShowHistory(MainViewModel vm, DocumentTabViewModel tab, Ultiviewer.Core.History.VersionHistory store, HistoryStep step)
    {
        var model = new HistoryViewModel(vm, tab, store);
        var window = new Ultiviewer.App.Views.HistoryWindow(model)
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
        };
        window.Show();
        Pump(300);
        Console.WriteLine("versions: " + string.Join(" | ", model.Versions.Select(v => $"{v.Label} · {v.Detail}")));
        if (step.Select is { } index)
        {
            model.Selected = model.Versions[index];
        }

        model.ShowChanges = !step.Whole;
        Pump(300);
        Console.WriteLine("summary: " + model.Summary);
        if (step.Shot is { } shot)
        {
            Save(window, shot);
            Console.WriteLine($"saved {shot}");
        }

        if (step.Restore)
        {
            model.Restore();
            Pump(200);
            Console.WriteLine("restored; versions now: " + model.Versions.Count);
        }

        window.Close();
    }

    /// <summary>
    /// Raises a left click on the n-th visual of a type (by class name) inside the text view, e.g. a checkbox or a
    /// table cell border. Mouse position can't be faked, so this only suits elements that handle the click themselves.
    /// </summary>
    private static void Click(TextEditor editor, string typeName, int index)
    {
        var matches = Descendants(editor.TextArea.TextView).Where(v => v.GetType().Name == typeName).ToList();
        if (index >= matches.Count)
        {
            throw new InvalidOperationException($"{typeName} #{index} not found ({matches.Count} present)");
        }

        var target = (UIElement)matches[index];
        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }

    private static void Save(Window window, string path)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var width = (int)Math.Ceiling(content.ActualWidth);
        var height = (int)Math.Ceiling(content.ActualHeight);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(ThemeService.Instance.Palette.Background), null, new Rect(0, 0, width, height));
            // An absolute viewbox maps the content 1:1 (the default one stretches its drawn bounds, which can be stale).
            var brush = new VisualBrush(content) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, width, height) };
            context.DrawRectangle(brush, null, new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>Pumps the dispatcher until a task finishes (or the timeout passes).</summary>
    private static void Wait(Task task, int timeoutMs)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted && watch.ElapsedMilliseconds < timeoutMs)
        {
            Pump(100);
        }

        if (!task.IsCompleted)
        {
            throw new TimeoutException("export did not finish");
        }

        task.GetAwaiter().GetResult();
    }

    /// <summary>Runs the dispatcher (layout, rendering, timers) for a while.</summary>
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(milliseconds), DispatcherPriority.Background, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }
}

/// <summary>Dialogs never appear in snapshots: messages go to the console and confirmations follow the script.</summary>
internal sealed class ScriptDialogs : IDialogService
{
    public bool ConfirmAnswer { get; set; }

    public void ShowError(string message) => Console.WriteLine("error dialog: " + message);

    public void ShowInfo(string message) => Console.WriteLine("info dialog: " + message);

    public bool Confirm(string message, bool warning = false)
    {
        Console.WriteLine($"confirm ({(ConfirmAnswer ? "yes" : "no")}): {message}");
        return ConfirmAnswer;
    }

    public bool? AskYesNoCancel(string message)
    {
        Console.WriteLine("yes/no/cancel (cancel): " + message);
        return null;
    }

    public string? PickFolder(string title, string? initialFolder = null) => null;

    public string? PickFile(string title, string filter = "All files|*.*") => null;

    public string? PickSaveFile(string title, string fileName, string filter) => null;

    public string? PromptAnswer { get; set; }

    public string? Prompt(string title, string label, string initial = "", Func<string, string?>? validate = null)
    {
        Console.WriteLine($"prompt ({PromptAnswer ?? "cancel"}): {title} [{initial}]");
        return PromptAnswer;
    }
}

/// <summary>A snapshot script: where state lives, the window, the file to open and the steps to run.</summary>
internal sealed class Script
{
    public string Home { get; set; } = Path.Combine(Path.GetTempPath(), "uvsnap", "home");

    public string? File { get; set; }

    public string Theme { get; set; } = "light";

    public double Width { get; set; } = 1200;

    public double Height { get; set; } = 800;

    public List<Step> Steps { get; set; } = [];
}

/// <summary>One step; every property is optional and they run in the order listed here.</summary>
internal sealed class Step
{
    /// <summary>"right" or "down" opens a second editor, "none" goes back to one.</summary>
    public string? Split { get; set; }

    /// <summary>0 or 1: which editor the next steps act on.</summary>
    public int? FocusGroup { get; set; }

    public bool MoveToOther { get; set; }

    public string? Open { get; set; }

    public string? Theme { get; set; }

    public string? Mode { get; set; }

    /// <summary>Puts the caret at the first occurrence of this text, plus <see cref="CaretDelta"/>.</summary>
    public string? CaretAt { get; set; }

    public int CaretDelta { get; set; }

    public int? Caret { get; set; }

    public int[]? Select { get; set; }

    public string? Type { get; set; }

    /// <summary>Saves the active tab (as autosave would).</summary>
    public bool Save { get; set; }

    /// <summary>Overwrites the active file on disk with this text, as another program would, and checks for changes.</summary>
    public string? External { get; set; }

    /// <summary>The answer to confirmation dialogs from this step on.</summary>
    public bool? Confirm { get; set; }

    public HistoryStep? History { get; set; }

    /// <summary>Opens today's daily note.</summary>
    public bool Today { get; set; }

    /// <summary>Adds this text with the quick-note logic to <see cref="CaptureTarget"/>.</summary>
    public string? Capture { get; set; }

    public Ultiviewer.Core.Capture.CaptureTarget CaptureTarget { get; set; }

    /// <summary>Turns every unlinked mention in the Links pane into a link.</summary>
    public bool LinkMentions { get; set; }

    /// <summary>Runs a search and prints the results.</summary>
    public string? Search { get; set; }

    /// <summary>Exports the active page through the export window: writes this path + ".html" and + ".pdf".</summary>
    public string? Export { get; set; }

    /// <summary>Renders a reminder card for the first task with a reminder (off-screen, not activated).</summary>
    public string? ReminderWindow { get; set; }

    /// <summary>Renders the quick-note window (off-screen, not activated) to this PNG.</summary>
    public string? CaptureWindow { get; set; }

    /// <summary>Live view shows Markdown marks at the caret (true) or keeps them hidden (false).</summary>
    public bool? MarksAtCaret { get; set; }

    /// <summary>Sets the active document's text direction: "rtl", "ltr" or "auto".</summary>
    public string? Direction { get; set; }

    /// <summary>Pastes this HTML (or CF_HTML) into the active editor as a formatted paste, without the real clipboard.</summary>
    public string? PasteHtml { get; set; }

    /// <summary>Clips this web address or .html file into a new page.</summary>
    public string? Clip { get; set; }

    /// <summary>Shows search results as a table (true) or lines (false).</summary>
    public bool? Table { get; set; }

    /// <summary>Saves the current search; the name comes from <see cref="Prompt"/>.</summary>
    public bool SaveSearch { get; set; }

    /// <summary>The answer to text prompts from this step on.</summary>
    public string? Prompt { get; set; }

    /// <summary>Runs the saved search with this name.</summary>
    public string? RunSaved { get; set; }

    /// <summary>Sorts the results table by a column key ("title", "modified", "prop:status"…); again reverses.</summary>
    public string? SortBy { get; set; }

    /// <summary>Shortcut changes as made in Settings (command name → gesture; "" removes, "default" restores).</summary>
    public Dictionary<string, string>? Shortcuts { get; set; }

    /// <summary>Renders the settings window's shortcut list after <see cref="Shortcuts"/> to this PNG.</summary>
    public string? ShortcutsShot { get; set; }

    public PaletteStep? Palette { get; set; }

    public OneNoteStep? OneNote { get; set; }

    public string? Key { get; set; }

    /// <summary>An <c>EditorCommands</c> property name to run on the focused editor (e.g. "FoldAll", "MoveLineUp").</summary>
    public string? Command { get; set; }

    /// <summary>Selects a tab of the left panel (0 Notebooks, 1 Tags, 2 To-do, 3 Outline).</summary>
    public int? LeftTab { get; set; }

    /// <summary>Class name of a visual in the text view to click (see <see cref="Program"/>), with <see cref="ClickIndex"/>.</summary>
    public string? Click { get; set; }

    public int ClickIndex { get; set; }

    public double? Scroll { get; set; }

    public int? Wait { get; set; }

    public string? Shot { get; set; }

    public string? Dump { get; set; }

    public string? Print { get; set; }

    /// <summary>Writes settings and the session (open tabs, split) as closing the window would.</summary>
    public bool SaveState { get; set; }
}

/// <summary>Opens a OneNote file in the import window (see <see cref="Program"/>).</summary>
internal sealed class OneNoteStep
{
    public string Path { get; set; } = "";

    public double Width { get; set; } = 1120;

    public double Height { get; set; } = 780;

    /// <summary>Tree paths ("Group/Section/Page") to uncheck.</summary>
    public List<string> Uncheck { get; set; } = [];

    /// <summary>Tree path of the item to select.</summary>
    public string? Select { get; set; }

    public string? Shot { get; set; }

    public string? PreviewShot { get; set; }

    public string? MarkdownShot { get; set; }

    /// <summary>The folder for the new notebook.</summary>
    public string? Into { get; set; }

    public string? Name { get; set; }

    public bool Import { get; set; }

    public string? ReportShot { get; set; }
}

/// <summary>Opens the command palette with a query.</summary>
internal sealed class PaletteStep
{
    public string Query { get; set; } = "";

    /// <summary>How many of the shown commands to print.</summary>
    public int Count { get; set; } = 10;

    public string? Shot { get; set; }

    /// <summary>Runs the first shown command.</summary>
    public bool Run { get; set; }
}

/// <summary>Opens the active tab's history window.</summary>
internal sealed class HistoryStep
{
    /// <summary>Index in the version list to select.</summary>
    public int? Select { get; set; }

    /// <summary>Show the whole version instead of the changes.</summary>
    public bool Whole { get; set; }

    public bool Restore { get; set; }

    public string? Shot { get; set; }
}
