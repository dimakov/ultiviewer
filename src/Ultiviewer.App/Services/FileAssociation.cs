using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Ultiviewer.App.Services;

/// <summary>
/// Per-user registration (HKCU, no admin rights) that puts Ultiviewer in Explorer's "Open with" list for
/// Markdown and text files. Windows doesn't let apps make themselves the default silently; the user picks
/// Ultiviewer once in "Open with → Choose another app → Always".
/// </summary>
public static class FileAssociation
{
    public const string ProgId = "Ultiviewer.Document";
    private static readonly string[] Extensions = [".md", ".markdown", ".txt"];

    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Ultiviewer.exe");

    public static bool IsRegistered()
    {
        using var command = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command");
        return command?.GetValue(null) is string value && value.Contains(ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    public static void Register()
    {
        var exe = ExecutablePath;
        using (var progId = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            progId.SetValue(null, "Ultiviewer document");
            using var icon = progId.CreateSubKey("DefaultIcon");
            icon.SetValue(null, $"\"{exe}\",0");
            using var command = progId.CreateSubKey(@"shell\open\command");
            command.SetValue(null, $"\"{exe}\" \"%1\"");
        }

        using (var app = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\Ultiviewer.exe"))
        {
            app.SetValue("FriendlyAppName", "Ultiviewer");
            using var command = app.CreateSubKey(@"shell\open\command");
            command.SetValue(null, $"\"{exe}\" \"%1\"");
            using var types = app.CreateSubKey("SupportedTypes");
            foreach (var extension in Extensions)
            {
                types.SetValue(extension, "");
            }
        }

        foreach (var extension in Extensions)
        {
            using var openWith = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{extension}\OpenWithProgids");
            openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        NotifyShell();
    }

    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Applications\Ultiviewer.exe", throwOnMissingSubKey: false);
        foreach (var extension in Extensions)
        {
            using var openWith = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{extension}\OpenWithProgids", writable: true);
            openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
        }

        NotifyShell();
    }

    private static void NotifyShell() => SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
