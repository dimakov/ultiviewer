namespace Ultiviewer.Core.Settings;

/// <summary>
/// Where per-user state lives. Setting the <c>ULTIVIEWER_HOME</c> environment variable redirects
/// everything into one folder, which keeps test runs away from the real settings.
/// </summary>
public static class AppPaths
{
    private static readonly string? Home = Environment.GetEnvironmentVariable("ULTIVIEWER_HOME");

    /// <summary>Roaming: settings.</summary>
    public static string RoamingDirectory { get; } = Home is { Length: > 0 }
        ? Path.Combine(Home, "Roaming")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.Name);

    /// <summary>Local: session, search index, backups, WebView2 data.</summary>
    public static string LocalDirectory { get; } = Home is { Length: > 0 }
        ? Path.Combine(Home, "Local")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.Name);

    public static string SettingsFile => Path.Combine(RoamingDirectory, "settings.json");

    public static string SessionFile => Path.Combine(LocalDirectory, "session.json");

    public static string IndexDirectory => Path.Combine(LocalDirectory, "Index");

    public static string BackupDirectory => Path.Combine(LocalDirectory, "backup");

    public static string WebViewDataDirectory => Path.Combine(LocalDirectory, "WebView2");

    /// <summary>Default parent folder for notebooks created in the app.</summary>
    public static string DefaultNotebooksDirectory => Home is { Length: > 0 }
        ? Path.Combine(Home, "Notebooks")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), AppInfo.Name);
}
