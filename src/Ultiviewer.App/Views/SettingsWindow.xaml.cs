using System.Windows;
using Ultiviewer.App.Services;
using Ultiviewer.App.ViewModels;
using Ultiviewer.Core.Settings;

namespace Ultiviewer.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _settings;

    public SettingsWindow(SettingsViewModel settings)
    {
        InitializeComponent();
        _settings = settings;
        DataContext = settings;
    }

    private void OnSave(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnAssociate(object sender, RoutedEventArgs e) => RunRegistry(FileAssociation.Register);

    private void OnUnassociate(object sender, RoutedEventArgs e) => RunRegistry(FileAssociation.Unregister);

    private void RunRegistry(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            MessageBox.Show(this, ex.Message, Core.AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        _settings.IsAssociated = FileAssociation.IsRegistered();
    }

    private void OnOpenSettingsFolder(object sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(AppPaths.RoamingDirectory);
        ShellService.Open(AppPaths.RoamingDirectory);
    }
}
