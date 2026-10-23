using System.Text;
using Ultiviewer.Core.Files;

namespace Ultiviewer.Core.Tests.Files;

public class TextFileFormatTests
{
    [Theory]
    [InlineData("utf-8", false, "UTF-8")]
    [InlineData("utf-8", true, "UTF-8 BOM")]
    [InlineData("utf-16", true, "UTF-16 LE BOM")]
    [InlineData("utf-16BE", true, "UTF-16 BE BOM")]
    public void RoundTripsByteForByte(string encodingName, bool bom, string expectedName)
    {
        var encoding = Encoding.GetEncoding(encodingName);
        var text = "Line one\r\nשורה שתיים — ünïcödé\r\nthree";
        var bytes = (bom ? encoding.GetPreamble() : []).Concat(encoding.GetBytes(text)).ToArray();

        var decoded = TextFileFormat.Decode(bytes, out var format);

        Assert.Equal(text, decoded);
        Assert.Equal(expectedName, format.EncodingName);
        Assert.Equal(LineEnding.CrLf, format.LineEnding);
        Assert.Equal(bytes, format.Encode(decoded));
    }

    [Fact]
    public void DetectsBomlessUtf16()
    {
        var bytes = new UnicodeEncoding(false, false).GetBytes("plain ascii text\nsecond line");

        var decoded = TextFileFormat.Decode(bytes, out var format);

        Assert.Equal("plain ascii text\nsecond line", decoded);
        Assert.Equal(1200, format.Encoding.CodePage);
        Assert.False(format.HasBom);
        Assert.Equal(LineEnding.Lf, format.LineEnding);
    }

    [Fact]
    public void InvalidUtf8FallsBackToAnsi()
    {
        var bytes = new byte[] { (byte)'c', (byte)'a', (byte)'f', 0xE9 }; // "café" in Windows-1252

        TextFileFormat.Decode(bytes, out var format);

        Assert.Equal(TextFileFormat.Ansi.CodePage, format.Encoding.CodePage);
        Assert.Equal(bytes, format.Encode(TextFileFormat.Decode(bytes, out _)));
    }

    [Fact]
    public void LineEndingDetectionAndNormalization()
    {
        Assert.Null(TextFileFormat.DetectLineEnding("no breaks"));
        Assert.Equal(LineEnding.Lf, TextFileFormat.DetectLineEnding("a\nb\nc\r\nd"));
        Assert.Equal("a\r\nb\r\nc", TextFileFormat.NormalizeLineEndings("a\nb\rc", LineEnding.CrLf));
    }
}
