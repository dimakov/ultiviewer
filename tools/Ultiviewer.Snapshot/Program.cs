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

        var editor = window.EditorView.TextEditor;
        try
        {
            foreach (var step in script.Steps)
            {
                dialogs.ConfirmAnswer = step.Confirm ?? dialogs.ConfirmAnswer;
                Run(step, vm, window, editor);
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

    private static void Run(Step step, MainViewModel vm, MainWindow window, TextEditor editor)
    {
        if (step.Open is { } open)
        {
            vm.OpenDocument(open, preview: false);
            Pump(500);
        }

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

        if (step.Print is { } expression)
        {
            Console.WriteLine(expression switch
            {
                "caret" => $"caret {editor.CaretOffset} line {editor.TextArea.Caret.Line} col {editor.TextArea.Caret.Column}",
                "margin" => $"textview margin {editor.TextArea.TextView.Margin}",
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
            context.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        encoder.Save(stream);
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

    public string? Prompt(string title, string label, string initial = "", Func<string, string?>? validate = null) => null;
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

    /// <summary>Renders the quick-note window (off-screen, not activated) to this PNG.</summary>
    public string? CaptureWindow { get; set; }

    public string? Key { get; set; }

    /// <summary>Class name of a visual in the text view to click (see <see cref="Program"/>), with <see cref="ClickIndex"/>.</summary>
    public string? Click { get; set; }

    public int ClickIndex { get; set; }

    public double? Scroll { get; set; }

    public int? Wait { get; set; }

    public string? Shot { get; set; }

    public string? Dump { get; set; }

    public string? Print { get; set; }
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
