using System.Text;

namespace Ultiviewer.Core.Files;

/// <summary>
/// Snapshots of unsaved buffers (Notepad++-style "remember unsaved changes"). Each open document that has
/// unsaved text gets one UTF-8 file in the backup folder; the session records which file belongs to which tab.
/// </summary>
public sealed class BackupStore(string folder)
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public string Folder { get; } = folder;

    public static string NewName() => Guid.NewGuid().ToString("N") + ".txt";

    public void Write(string name, string text)
    {
        Directory.CreateDirectory(Folder);
        AtomicFile.WriteAllText(Path.Combine(Folder, name), text, Utf8);
    }

    public string? Read(string name)
    {
        var path = Path.Combine(Folder, name);
        try
        {
            return File.Exists(path) ? File.ReadAllText(path, Utf8) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Delete(string name)
    {
        try
        {
            File.Delete(Path.Combine(Folder, name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Removes backups that no tab refers to any more.</summary>
    public void DeleteAllExcept(IEnumerable<string> keep)
    {
        if (!Directory.Exists(Folder))
        {
            return;
        }

        var keepSet = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(Folder, "*.txt"))
        {
            if (!keepSet.Contains(Path.GetFileName(file)))
            {
                Delete(Path.GetFileName(file));
            }
        }
    }
}
