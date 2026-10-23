using System.Security.Cryptography;

namespace Ultiviewer.Core.Files;

/// <summary>Last-write time and size of a file, used to notice changes made by other programs.</summary>
public readonly record struct FileStamp(DateTime LastWriteUtc, long Length)
{
    public static FileStamp? Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new FileStamp(info.LastWriteTimeUtc, info.Length) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>A text file as loaded from disk.</summary>
public sealed record LoadedText(string Text, TextFileFormat Format, FileStamp Stamp, string Hash);

public enum LoadProblem
{
    None,
    Binary,
    TooLarge,
}

public static class TextDocumentFile
{
    /// <summary>Files above this size are refused (AvalonEdit stays responsive well below it).</summary>
    public const long MaxSize = 256L * 1024 * 1024;

    /// <summary>Sizes above this ask for confirmation first.</summary>
    public const long LargeSize = 32L * 1024 * 1024;

    public static LoadedText Load(string path, LineEnding defaultLineEnding = LineEnding.CrLf)
    {
        var bytes = ReadAllBytesShared(path);
        var text = TextFileFormat.Decode(bytes, out var format, defaultLineEnding);
        var stamp = FileStamp.Read(path) ?? new FileStamp(DateTime.MinValue, bytes.Length);
        return new LoadedText(text, format, stamp, Hash(bytes));
    }

    /// <summary>Saves atomically and returns the new stamp and content hash.</summary>
    public static (FileStamp Stamp, string Hash) Save(string path, string text, TextFileFormat format)
    {
        var bytes = format.Encode(text);
        AtomicFile.WriteAllBytes(path, bytes);
        return (FileStamp.Read(path) ?? new FileStamp(DateTime.UtcNow, bytes.Length), Hash(bytes));
    }

    /// <summary>Checks whether a file looks like text that the editor should open.</summary>
    public static LoadProblem Inspect(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxSize)
        {
            return LoadProblem.TooLarge;
        }

        Span<byte> head = stackalloc byte[8192];
        int read;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        }

        return LooksBinary(head[..read]) ? LoadProblem.Binary : LoadProblem.None;
    }

    /// <summary>NUL bytes outside a UTF-16/32 pattern mean binary data.</summary>
    public static bool LooksBinary(ReadOnlySpan<byte> head)
    {
        if (head.IsEmpty)
        {
            return false;
        }

        var (encoding, _) = TextFileFormat.DetectEncoding(head);
        if (encoding.CodePage is 1200 or 1201 or 12000)
        {
            return false;
        }

        return head.Contains((byte)0);
    }

    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static string HashFile(string path) => Hash(ReadAllBytesShared(path));

    /// <summary>Reads a file even while another program has it open for writing.</summary>
    public static byte[] ReadAllBytesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[stream.Length];
        stream.ReadExactly(buffer);
        return buffer;
    }
}
