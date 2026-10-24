using System.Windows;
using System.Windows.Input;
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

    /// <summary>Scrolls to the keyboard shortcuts (Help › Keyboard shortcuts) and focuses their filter box.</summary>
    public void ShowShortcuts()
    {
        ShortcutList.BringIntoView();
        ShortcutFilterBox.Focus();
    }

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

    /// <summary>Records the pressed key combination as the row's shortcut (Tab, Esc and Enter keep their usual jobs).</summary>
    private void OnShortcutKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ShortcutRow row })
        {
            return;
        }

        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key,
        };
        var modifiers = Keyboard.Modifiers;
        if (modifiers is ModifierKeys.None or ModifierKeys.Shift && key is Key.Tab or Key.Escape or Key.Enter)
        {
            return;
        }

        e.Handled = true;
        if (modifiers == ModifierKeys.None && key is Key.Back or Key.Delete)
        {
            _settings.SetShortcut(row, "");
        }
        else if (KeyboardShortcuts.GestureFor(key, modifiers) is { } gesture)
        {
            _settings.SetShortcut(row, gesture);
        }
    }

    private void OnResetShortcut(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ShortcutRow row })
        {
            _settings.ResetShortcut(row);
        }
    }

    private void OnResetAllShortcuts(object sender, RoutedEventArgs e) => _settings.ResetAllShortcuts();

    private void OnOpenSettingsFolder(object sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(AppPaths.RoamingDirectory);
        ShellService.Open(AppPaths.RoamingDirectory);
    }
}
