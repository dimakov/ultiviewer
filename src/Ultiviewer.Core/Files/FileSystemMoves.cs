namespace Ultiviewer.Core.Files;

/// <summary>Moves that also work for case-only renames and across volumes.</summary>
public static class FileSystemMoves
{
    public static void MoveFile(string source, string destination)
    {
        if (IsCaseOnlyChange(source, destination))
        {
            var temp = destination + "." + Guid.NewGuid().ToString("N")[..8];
            File.Move(source, temp);
            File.Move(temp, destination);
            return;
        }

        File.Move(source, destination);
    }

    public static void MoveDirectory(string source, string destination)
    {
        if (IsCaseOnlyChange(source, destination))
        {
            var temp = destination + "." + Guid.NewGuid().ToString("N")[..8];
            Directory.Move(source, temp);
            Directory.Move(temp, destination);
            return;
        }

        if (string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(source, destination);
            return;
        }

        CopyDirectory(source, destination);
        Directory.Delete(source, recursive: true);
    }

    public static bool IsSamePath(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="path"/> is <paramref name="ancestor"/> or inside it.</summary>
    public static bool IsSameOrInside(string path, string ancestor)
    {
        var p = Normalize(path);
        var a = Normalize(ancestor);
        return p.Equals(a, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCaseOnlyChange(string source, string destination) =>
        IsSamePath(source, destination) && !string.Equals(source, destination, StringComparison.Ordinal);

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}
