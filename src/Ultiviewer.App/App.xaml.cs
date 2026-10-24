using System.Windows;
using System.Windows.Threading;
using Ultiviewer.App.Services;
using Ultiviewer.App.ViewModels;
using Ultiviewer.Core.Notebooks;
using Ultiviewer.Core.Settings;

namespace Ultiviewer.App;

public partial class App : Application
{
    private MainViewModel? _viewModel;
    private SingleInstance? _instance;
    private GlobalHotkey? _hotkey;
    private TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        var files = e.Args.Where(arg => !arg.StartsWith("--", StringComparison.Ordinal)).ToList();
        _instance = SingleInstance.Acquire(files);
        if (_instance is null)
        {
            Shutdown(); // the running instance opens the files
            return;
        }

        var settings = AppSettings.Load();
        KeyboardShortcuts.ApplyEditorOverrides(settings.Shortcuts);
        var session = SessionState.Load();
        ThemeService.Instance.Apply(settings.Theme);

        _viewModel = new MainViewModel(settings, session, new DialogService(), new NotebookOperations(new RecycleBinDeleter()));
        var window = new MainWindow(_viewModel);
        MainWindow = window;
        window.Show();
        _viewModel.Initialize(files);
        _instance.Listen(paths => Dispatcher.BeginInvoke(() => window.OpenFromAnotherLaunch(paths)));

        // Windows-wide quick-note shortcut and the optional tray icon live here, not in MainWindow, so tools
        // that host the window (the snapshot tool) never grab a system shortcut.
        _hotkey = new GlobalHotkey(new System.Windows.Interop.WindowInteropHelper(window).Handle);
        ApplyCaptureSettings();
        _viewModel.CaptureSettingsChanged += ApplyCaptureSettings;
        SessionEnding += (_, _) => _viewModel.ExitRequested = true;
    }

    private void ApplyCaptureSettings()
    {
        if (_viewModel is not { } vm || _hotkey is null || MainWindow is not MainWindow window)
        {
            return;
        }

        var hotkey = vm.Settings.QuickCaptureHotkey;
        if (!_hotkey.Register(hotkey, () => vm.ShowQuickNoteCommand.Execute(null)))
        {
            vm.StatusMessage = $"Couldn't use {hotkey} for quick notes (another program may use it). Choose another shortcut in Settings.";
        }

        if (vm.Settings.KeepRunningInTray && _tray is null)
        {
            _tray = new TrayIcon(
                open: window.ShowFromTray,
                quickNote: () => vm.ShowQuickNoteCommand.Execute(null),
                today: () =>
                {
                    window.ShowFromTray();
                    vm.OpenTodayCommand.Execute(null);
                },
                exit: () =>
                {
                    vm.ExitRequested = true;
                    window.Close();
                });
        }
        else if (!vm.Settings.KeepRunningInTray && _tray is not null)
        {
            _tray.Dispose();
            _tray = null;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkey?.Dispose();
        _tray?.Dispose();
        _viewModel?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private static bool _reporting;

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ErrorLog.Write(e.Exception, "Unhandled UI exception");
        e.Handled = true;

        // A failure that repeats on every layout pass must not stack message boxes (each one pumps messages).
        if (_reporting)
        {
            return;
        }

        _reporting = true;
        try
        {
            ShowError(e.Exception);
        }
        finally
        {
            _reporting = false;
        }
    }

    private static void ShowError(Exception exception) =>
        MessageBox.Show(
            $"Something went wrong:\n\n{exception.Message}\n\nDetails were written to {ErrorLog.FilePath}.",
            Core.AppInfo.Name,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
}
