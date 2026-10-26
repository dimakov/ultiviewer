using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Ultiviewer.Core.OneNote.Store;

namespace Ultiviewer.Core.OneNote;

/// <summary>
/// Extracts a Microsoft cabinet (.cab), the container of OneNote's <c>.onepkg</c> notebook packages
/// ([MS-CAB]). Uncompressed and MSZIP folders are read here; other compression (LZX, Quantum) is left to
/// Windows' <c>tar.exe</c>. Entries that would land outside the destination folder are skipped.
/// </summary>
public static class Cabinet
{
    private const uint Signature = 0x4643534D; // "MSCF"
    private const int HistorySize = 32 * 1024;

    /// <summary>Extracts every file of the cabinet at <paramref name="path"/> into <paramref name="destination"/>.</summary>
    public static void Extract(string path, string destination)
    {
        Directory.CreateDirectory(destination);
        try
        {
            using var stream = File.OpenRead(path);
            ExtractFrom(stream, destination);
        }
        catch (UnsupportedCompressionException)
        {
            ExtractWithTar(path, destination);
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException)
        {
            throw new OneNoteFormatException("The package is damaged: " + ex.Message);
        }
    }

    private static void ExtractFrom(Stream stream, string destination)
    {
        var reader = new BinaryReader(stream, Encoding.Latin1, leaveOpen: true);
        if (stream.Length < 36 || reader.ReadUInt32() != Signature)
        {
            throw new OneNoteFormatException("This is not a OneNote package (.onepkg) file.");
        }

        reader.ReadUInt32();
        reader.ReadUInt32(); // cbCabinet
        reader.ReadUInt32();
        var filesOffset = reader.ReadUInt32();
        reader.ReadUInt32();
        reader.ReadUInt16(); // version
        var folderCount = reader.ReadUInt16();
        var fileCount = reader.ReadUInt16();
        var flags = reader.ReadUInt16();
        reader.ReadUInt32(); // setID, iCabinet
        if ((flags & 0x0003) != 0)
        {
            throw new OneNoteFormatException("The package is split over several files, which the import doesn't read.");
        }

        int folderReserve = 0, dataReserve = 0;
        if ((flags & 0x0004) != 0)
        {
            var headerReserve = reader.ReadUInt16();
            folderReserve = reader.ReadByte();
            dataReserve = reader.ReadByte();
            stream.Seek(headerReserve, SeekOrigin.Current);
        }

        var folders = new List<(uint DataOffset, int Blocks, int Compression)>();
        for (var i = 0; i < folderCount; i++)
        {
            folders.Add((reader.ReadUInt32(), reader.ReadUInt16(), reader.ReadUInt16()));
            stream.Seek(folderReserve, SeekOrigin.Current);
        }

        stream.Position = filesOffset;
        var files = new List<Entry>();
        for (var i = 0; i < fileCount; i++)
        {
            var size = reader.ReadUInt32();
            var offset = reader.ReadUInt32();
            var folder = reader.ReadUInt16();
            reader.ReadUInt32(); // date, time
            reader.ReadUInt16(); // attributes
            var name = ReadName(reader);
            if (folder < folders.Count && SafeTarget(destination, name) is { } target)
            {
                files.Add(new Entry(target, folder, offset, size));
            }
        }

        if (folders.Any(f => (f.Compression & 0x0F) is not (0 or 1)))
        {
            throw new UnsupportedCompressionException();
        }

        for (var f = 0; f < folders.Count; f++)
        {
            var entries = files.Where(e => e.Folder == f).OrderBy(e => e.Offset).ToList();
            if (entries.Count > 0)
            {
                ExtractFolder(stream, reader, folders[f].DataOffset, folders[f].Blocks, (folders[f].Compression & 0x0F) == 1, dataReserve, entries);
            }
        }
    }

    private static void ExtractFolder(Stream stream, BinaryReader reader, uint dataOffset, int blocks, bool mszip, int dataReserve, List<Entry> entries)
    {
        stream.Position = dataOffset;
        var history = Array.Empty<byte>();
        long position = 0;
        var next = 0;
        FileStream? output = null;
        try
        {
            for (var b = 0; b < blocks && next < entries.Count; b++)
            {
                reader.ReadUInt32(); // checksum
                var compressedSize = reader.ReadUInt16();
                var size = reader.ReadUInt16();
                stream.Seek(dataReserve, SeekOrigin.Current);
                var data = reader.ReadBytes(compressedSize);
                if (data.Length != compressedSize)
                {
                    throw new EndOfStreamException("a data block is cut short.");
                }

                var block = mszip ? Inflate(data, history, size) : data;
                if (mszip)
                {
                    history = Tail(history, block);
                }

                // Hand the block's bytes to the files it covers (files are contiguous within a folder).
                var end = position + block.Length;
                while (next < entries.Count && entries[next].Offset <= end)
                {
                    var entry = entries[next];
                    output ??= Create(entry.Target);
                    var from = Math.Max(entry.Offset, position);
                    var to = Math.Min(entry.Offset + entry.Size, end);
                    if (to > from)
                    {
                        output.Write(block, (int)(from - position), (int)(to - from));
                    }

                    if (entry.Offset + entry.Size > end)
                    {
                        break;
                    }

                    output.Dispose();
                    output = null;
                    next++;
                }

                position = end;
            }

            for (; next < entries.Count && entries[next].Size == 0 && entries[next].Offset <= position; next++)
            {
                Create(entries[next].Target).Dispose(); // empty files at the very end
            }

            if (next < entries.Count)
            {
                throw new EndOfStreamException("a file's data is missing.");
            }
        }
        finally
        {
            output?.Dispose();
        }
    }

    /// <summary>
    /// Inflates one MSZIP block. MSZIP keeps the previous 32 KB of output as history for back-references, which
    /// <see cref="DeflateStream"/> can't be given directly, so the history goes first as a stored deflate block
    /// (the compressed block starts on a byte boundary, as stored data ends on one) and is then skipped.
    /// </summary>
    private static byte[] Inflate(byte[] data, byte[] history, int size)
    {
        if (data.Length < 2 || data[0] != (byte)'C' || data[1] != (byte)'K')
        {
            throw new InvalidDataException("a compressed block has no MSZIP signature.");
        }

        var input = new byte[5 + history.Length + data.Length - 2];
        input[1] = (byte)history.Length;
        input[2] = (byte)(history.Length >> 8);
        input[3] = (byte)~history.Length;
        input[4] = (byte)(~history.Length >> 8);
        history.CopyTo(input, 5);
        data.AsSpan(2).CopyTo(input.AsSpan(5 + history.Length));

        var output = new byte[history.Length + size];
        using var deflate = new DeflateStream(new MemoryStream(input), CompressionMode.Decompress);
        deflate.ReadExactly(output);
        return output[history.Length..];
    }

    private static byte[] Tail(byte[] history, byte[] block)
    {
        if (block.Length >= HistorySize)
        {
            return block[^HistorySize..];
        }

        var keep = Math.Min(history.Length, HistorySize - block.Length);
        return [.. history[^keep..], .. block];
    }

    private static FileStream Create(string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        return new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
    }

    /// <summary>A file name: UTF-8 when flagged (or when it decodes as UTF-8), else the code page, read as Latin-1.</summary>
    private static string ReadName(BinaryReader reader)
    {
        var bytes = new List<byte>();
        for (byte b; (b = reader.ReadByte()) != 0 && bytes.Count < 1024;)
        {
            bytes.Add(b);
        }

        var raw = bytes.ToArray();
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(raw);
        }
    }

    /// <summary>The full path for a cabinet entry name, or null when it would leave <paramref name="destination"/>.</summary>
    internal static string? SafeTarget(string destination, string name)
    {
        var parts = name.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p is "." or ".." || p.Contains(':')))
        {
            return null;
        }

        var root = Path.GetFullPath(destination);
        var target = Path.GetFullPath(Path.Combine([root, .. parts]));
        return target.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? target : null;
    }

    private static void ExtractWithTar(string path, string destination)
    {
        // tar.exe (libarchive, in Windows since 2018) reads LZX and refuses paths that leave the destination.
        var tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
        if (!OperatingSystem.IsWindows() || !File.Exists(tar))
        {
            throw new OneNoteFormatException("The package uses a compression the import can't read.");
        }

        var start = new ProcessStartInfo(tar)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in new[] { "-xf", path, "-C", destination })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new OneNoteFormatException("Windows couldn't extract the package.");
        _ = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new OneNoteFormatException("Windows couldn't extract the package: " + error.Trim());
        }
    }

    private sealed record Entry(string Target, int Folder, long Offset, long Size);

    private sealed class UnsupportedCompressionException : Exception;
}
