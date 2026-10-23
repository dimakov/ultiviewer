using System.Diagnostics;
using System.IO;

namespace Ultiviewer.App.Services;

/// <summary>Hands things over to Windows: Explorer, the default browser, the default app for a file.</summary>
public static class ShellService
{
    public static void RevealInExplorer(string path)
    {
        if (File.Exists(path))
        {
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        else if (Directory.Exists(path))
        {
            Process.Start("explorer.exe", $"\"{path}\"");
        }
    }

    /// <summary>Opens a file or URL with its default handler.</summary>
    public static void Open(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
}
