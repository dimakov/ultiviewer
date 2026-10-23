using System.Globalization;
using System.Text;

namespace Ultiviewer.Core.Files;

public enum LineEnding
{
    CrLf,
    Lf,
    Cr,
}

/// <summary>
/// How a text file is stored on disk: encoding, byte-order mark and dominant line ending.
/// Files are written back in the format they were read in, so unchanged files round-trip byte for byte.
/// </summary>
public sealed record TextFileFormat(Encoding Encoding, bool HasBom, LineEnding LineEnding)
{
    static TextFileFormat()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static Encoding Utf8 { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static TextFileFormat Utf8Default(LineEnding lineEnding = LineEnding.CrLf) => new(Utf8, false, lineEnding);

    /// <summary>The system's legacy ANSI code page (Windows-1252, Windows-1255, …).</summary>
    public static Encoding Ansi => Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);

    public string EncodingName => Encoding.CodePage switch
    {
        65001 => HasBom ? "UTF-8 BOM" : "UTF-8",
        1200 => HasBom ? "UTF-16 LE BOM" : "UTF-16 LE",
        1201 => HasBom ? "UTF-16 BE BOM" : "UTF-16 BE",
        12000 => "UTF-32 LE BOM",
        _ => Encoding.WebName switch
        {
            var name when name.StartsWith("windows-", StringComparison.Ordinal) => "Windows-" + name["windows-".Length..],
            var name => name.ToUpperInvariant(),
        },
    };

    public string LineEndingName => LineEnding switch
    {
        LineEnding.CrLf => "CRLF",
        LineEnding.Lf => "LF",
        _ => "CR",
    };

    public string NewLine => LineEnding switch
    {
        LineEnding.CrLf => "\r\n",
        LineEnding.Lf => "\n",
        _ => "\r",
    };

    public byte[] Encode(string text)
    {
        var preamble = HasBom ? Preamble(Encoding.CodePage) : [];
        var bytes = new byte[preamble.Length + Encoding.GetByteCount(text)];
        preamble.CopyTo(bytes, 0);
        Encoding.GetBytes(text, 0, text.Length, bytes, preamble.Length);
        return bytes;
    }

    public static string Decode(ReadOnlySpan<byte> bytes, out TextFileFormat format, LineEnding defaultLineEnding = LineEnding.CrLf)
    {
        var (encoding, bomLength) = DetectEncoding(bytes);
        var text = encoding.GetString(bytes[bomLength..]);
        format = new TextFileFormat(encoding, bomLength > 0, DetectLineEnding(text) ?? defaultLineEnding);
        return text;
    }

    public static (Encoding Encoding, int BomLength) DetectEncoding(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return (Utf8, 3);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0x00, 0x00]))
        {
            return (new UTF32Encoding(bigEndian: false, byteOrderMark: false), 4);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: false), 2);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: false), 2);
        }

        if (LooksLikeUtf16(bytes, out var bigEndian))
        {
            return (new UnicodeEncoding(bigEndian, byteOrderMark: false), 0);
        }

        return (IsValidUtf8(bytes) ? Utf8 : Ansi, 0);
    }

    /// <summary>The most common line ending in <paramref name="text"/>, or null if it has none.</summary>
    public static LineEnding? DetectLineEnding(string text)
    {
        int crlf = 0, lf = 0, cr = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crlf++;
                    i++;
                }
                else
                {
                    cr++;
                }
            }
            else if (text[i] == '\n')
            {
                lf++;
            }
        }

        if (crlf == 0 && lf == 0 && cr == 0)
        {
            return null;
        }

        return crlf >= lf && crlf >= cr ? LineEnding.CrLf : lf >= cr ? LineEnding.Lf : LineEnding.Cr;
    }

    /// <summary>Rewrites every line break in <paramref name="text"/> as <paramref name="lineEnding"/>.</summary>
    public static string NormalizeLineEndings(string text, LineEnding lineEnding)
    {
        var newLine = lineEnding switch { LineEnding.CrLf => "\r\n", LineEnding.Lf => "\n", _ => "\r" };
        return text.ReplaceLineEndings(newLine);
    }

    private static bool IsValidUtf8(ReadOnlySpan<byte> bytes) => System.Text.Unicode.Utf8.IsValid(bytes);

    /// <summary>BOM-less UTF-16 is recognized by the zero high bytes of ASCII characters.</summary>
    private static bool LooksLikeUtf16(ReadOnlySpan<byte> bytes, out bool bigEndian)
    {
        bigEndian = false;
        var sample = bytes[..(Math.Min(bytes.Length, 4096) & ~1)];
        if (sample.Length < 4)
        {
            return false;
        }

        int evenZeros = 0, oddZeros = 0;
        for (var i = 0; i < sample.Length; i += 2)
        {
            if (sample[i] == 0) evenZeros++;
            if (sample[i + 1] == 0) oddZeros++;
        }

        var pairs = sample.Length / 2;
        if (oddZeros > pairs * 0.4 && evenZeros < pairs * 0.05)
        {
            return true;
        }

        if (evenZeros > pairs * 0.4 && oddZeros < pairs * 0.05)
        {
            bigEndian = true;
            return true;
        }

        return false;
    }

    private static byte[] Preamble(int codePage) => codePage switch
    {
        65001 => [0xEF, 0xBB, 0xBF],
        1200 => [0xFF, 0xFE],
        1201 => [0xFE, 0xFF],
        12000 => [0xFF, 0xFE, 0x00, 0x00],
        _ => [],
    };
}
