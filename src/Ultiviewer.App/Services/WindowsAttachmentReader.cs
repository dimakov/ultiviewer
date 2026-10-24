using System.IO;
using Ultiviewer.Core.Search;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;
using WinPdf = Windows.Data.Pdf;

namespace Ultiviewer.App.Services;

/// <summary>
/// Attachment text for the search index: PDF text layers (PdfPig), and with OCR on, Windows' built-in text
/// recognition (Windows.Media.Ocr, in the user's languages) for pictures and for PDF pages that have no text
/// (scans). Runs on a background thread.
/// </summary>
public sealed class WindowsAttachmentReader(bool ocr) : IAttachmentReader
{
    /// <summary>PDF pages with less text than this are treated as scans.</summary>
    private const int ScanThreshold = 16;

    private readonly PdfTextReader _pdf = new();
    private readonly Lazy<OcrEngine?> _engine = new(CreateEngine);

    public bool CanRead(string path) =>
        _pdf.CanRead(path) || (ocr && AttachmentFiles.ImageExtensions.Contains(Path.GetExtension(path)) && _engine.Value is not null);

    public IReadOnlyList<string> Read(string path, CancellationToken cancellationToken)
    {
        if (_pdf.CanRead(path))
        {
            var pages = _pdf.Read(path, cancellationToken).ToArray();
            if (ocr && _engine.Value is { } engine && pages.Any(p => p.Trim().Length < ScanThreshold))
            {
                OcrScannedPages(path, pages, engine, cancellationToken);
            }

            return pages;
        }

        if (_engine.Value is not { } imageEngine)
        {
            return [];
        }

        var file = StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken).GetAwaiter().GetResult();
        using var stream = file.OpenAsync(FileAccessMode.Read).AsTask(cancellationToken).GetAwaiter().GetResult();
        return [Recognize(BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).GetAwaiter().GetResult(), imageEngine)];
    }

    private static OcrEngine? CreateEngine() =>
        OcrEngine.TryCreateFromUserProfileLanguages() ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));

    private static void OcrScannedPages(string path, string[] pages, OcrEngine engine, CancellationToken cancellationToken)
    {
        var file = StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken).GetAwaiter().GetResult();
        var document = WinPdf.PdfDocument.LoadFromFileAsync(file).AsTask(cancellationToken).GetAwaiter().GetResult();
        for (var i = 0; i < pages.Length && i < document.PageCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pages[i].Trim().Length >= ScanThreshold)
            {
                continue;
            }

            using var page = document.GetPage((uint)i);
            using var stream = new InMemoryRandomAccessStream();

            // Render at about 200 dpi (PDF sizes are in 1/96 inch here), within OCR's size limit.
            var scale = Math.Min(200.0 / 96, OcrEngine.MaxImageDimension / Math.Max(page.Size.Width, page.Size.Height));
            page.RenderToStreamAsync(stream, new WinPdf.PdfPageRenderOptions { DestinationWidth = (uint)(page.Size.Width * scale) }).AsTask(cancellationToken).GetAwaiter().GetResult();
            var decoder = BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).GetAwaiter().GetResult();
            var text = Recognize(decoder, engine);
            if (text.Length > pages[i].Trim().Length)
            {
                pages[i] = text;
            }
        }
    }

    /// <summary>Recognizes the text of a picture, scaled down when larger than OCR accepts.</summary>
    private static string Recognize(BitmapDecoder decoder, OcrEngine engine)
    {
        var largest = Math.Max(decoder.PixelWidth, decoder.PixelHeight);
        var transform = new BitmapTransform();
        if (largest > OcrEngine.MaxImageDimension)
        {
            var scale = (double)OcrEngine.MaxImageDimension / largest;
            transform.ScaledWidth = (uint)(decoder.PixelWidth * scale);
            transform.ScaledHeight = (uint)(decoder.PixelHeight * scale);
        }

        using var bitmap = decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage)
            .AsTask().GetAwaiter().GetResult();
        var result = engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();
        return string.Join('\n', result.Lines.Select(l => l.Text));
    }
}
