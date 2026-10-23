using System.Windows;

namespace Ultiviewer.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ThemeMode = ThemeMode.System;
        new MainWindow().Show();
    }
}
