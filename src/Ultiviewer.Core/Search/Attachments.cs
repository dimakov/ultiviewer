using Ultiviewer.Core.Notebooks;
using UglyToad.PdfPig;

namespace Ultiviewer.Core.Search;

/// <summary>Reads the text of an attachment: one string per PDF page, or one for an image.</summary>
public interface IAttachmentReader
{
    bool CanRead(string path);

    /// <summary>The text of each page (or the image); empty strings for pages without text.</summary>
    IReadOnlyList<string> Read(string path, CancellationToken cancellationToken);
}

/// <summary>Files inside a notebook whose text is searchable: PDFs and pictures (pages' .assets folders included).</summary>
public static class AttachmentFiles
{
    public static readonly IReadOnlySet<string> PdfExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".pdf" };

    public static readonly IReadOnlySet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp",
    };

    public static bool IsAttachment(string path) =>
        PdfExtensions.Contains(Path.GetExtension(path)) || ImageExtensions.Contains(Path.GetExtension(path));

    /// <summary>Attachments under a notebook folder, skipping hidden folders (.git, .obsidian…).</summary>
    public static IEnumerable<string> Enumerate(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            IEnumerable<string> files, folders;
            try
            {
                files = Directory.EnumerateFiles(folder).ToList();
                folders = Directory.EnumerateDirectories(folder).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (IsAttachment(file))
                {
                    yield return file;
                }
            }

            foreach (var child in folders)
            {
                if (!Path.GetFileName(child).StartsWith('.'))
                {
                    pending.Push(child);
                }
            }
        }
    }
}

/// <summary>Text of PDF pages (text layers only; scanned pages come back empty).</summary>
public sealed class PdfTextReader : IAttachmentReader
{
    public bool CanRead(string path) => AttachmentFiles.PdfExtensions.Contains(Path.GetExtension(path));

    public IReadOnlyList<string> Read(string path, CancellationToken cancellationToken)
    {
        using var document = PdfDocument.Open(path);
        var pages = new List<string>(document.NumberOfPages);
        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            pages.Add(string.Join(' ', page.GetWords().Select(w => w.Text)));
        }

        return pages;
    }
}
