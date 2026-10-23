using System.Text;
using Ultiviewer.Core.Notebooks;

namespace Ultiviewer.Core.Files;

/// <summary>
/// Writes files so a crash never leaves a half-written file: the data goes to a temp file in the
/// same folder, which then replaces the target.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string text, Encoding encoding) =>
        WriteAllBytes(path, Encode(text, encoding));

    public static void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
    {
        var temp = path + NotebookScanner.TempSuffix;
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        try
        {
            if (File.Exists(path))
            {
                ReplaceExisting(temp, path);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static void ReplaceExisting(string temp, string path)
    {
        try
        {
            // Keeps the target's attributes, ACLs and creation time.
            File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Some file systems (network shares, cloud placeholders) don't support ReplaceFile.
            var attributes = File.GetAttributes(path);
            File.SetAttributes(path, attributes & ~(FileAttributes.Hidden | FileAttributes.ReadOnly));
            File.Copy(temp, path, overwrite: true);
            File.SetAttributes(path, attributes);
        }
    }

    private static byte[] Encode(string text, Encoding encoding)
    {
        var preamble = encoding.GetPreamble();
        var bytes = new byte[preamble.Length + encoding.GetByteCount(text)];
        preamble.CopyTo(bytes, 0);
        encoding.GetBytes(text, 0, text.Length, bytes, preamble.Length);
        return bytes;
    }
}
