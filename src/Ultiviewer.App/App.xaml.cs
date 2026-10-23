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
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ErrorLog.Write(e.Exception, "Unhandled UI exception");
        MessageBox.Show(
            $"Something went wrong:\n\n{e.Exception.Message}\n\nDetails were written to {ErrorLog.FilePath}.",
            Core.AppInfo.Name,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
