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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        var settings = AppSettings.Load();
        var session = SessionState.Load();
        ApplyTheme(settings.Theme);

        _viewModel = new MainViewModel(settings, session, new DialogService(), new NotebookOperations(new RecycleBinDeleter()));
        var window = new MainWindow(_viewModel);
        MainWindow = window;
        window.Show();
        _viewModel.Initialize();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.Dispose();
        base.OnExit(e);
    }

    public void ApplyTheme(ThemePreference theme) =>
        ThemeMode = theme switch
        {
            ThemePreference.Light => ThemeMode.Light,
            ThemePreference.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };

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
