namespace Ultiviewer.Core.Notebooks;

/// <summary>Deletes a file or folder. The app sends items to the Recycle Bin; tests delete permanently.</summary>
public interface IFileDeleter
{
    void Delete(string path);
}

public sealed class PermanentFileDeleter : IFileDeleter
{
    public void Delete(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
