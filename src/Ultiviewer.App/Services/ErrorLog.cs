using System.IO;
using Ultiviewer.Core.Settings;

namespace Ultiviewer.App.Services;

/// <summary>Appends unexpected exceptions to %LOCALAPPDATA%\Ultiviewer\logs\errors.log.</summary>
public static class ErrorLog
{
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(AppPaths.LocalDirectory, "logs", "errors.log");

    public static void Write(Exception exception, string context = "")
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, $"[{DateTime.Now:O}] {context}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // Logging must never take the app down.
        }
    }
}
