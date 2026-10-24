using System.IO;
using Microsoft.Data.Sqlite;
using Ultiviewer.App.Services;
using Ultiviewer.Core.Search;

namespace Ultiviewer.App.ViewModels;

/// <summary>
/// Text from PDFs and pictures for search: a low-priority background thread reads new or changed attachments a
/// few at a time (PDF text, and OCR when enabled) whenever notebooks change, and every 10 minutes regardless.
/// </summary>
public partial class MainViewModel
{
    private readonly AutoResetEvent _attachmentSignal = new(false);
    private readonly CancellationTokenSource _attachmentStop = new();
    private Thread? _attachmentThread;

    /// <summary>Starts the reader once; later calls ask it to look again.</summary>
    private void RequestAttachmentIndexing()
    {
        if (_attachmentThread is not null)
        {
            _attachmentSignal.Set();
            return;
        }

        _attachmentThread = new Thread(AttachmentLoop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Attachment text" };
        _attachmentThread.Start();
    }

    private void AttachmentLoop()
    {
        var token = _attachmentStop.Token;
        while (!token.IsCancellationRequested)
        {
            if (_search is { } search && Settings.SearchAttachments)
            {
                var reader = new WindowsAttachmentReader(Settings.OcrImages);
                var changed = false;
                foreach (var index in search.Indexes)
                {
                    try
                    {
                        int remaining;
                        do
                        {
                            (var read, remaining) = index.UpdateAttachments(reader, token, maxFiles: 5);
                            changed |= read > 0;
                        }
                        while (remaining > 0 && !token.IsCancellationRequested);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or ObjectDisposedException or InvalidOperationException)
                    {
                        ErrorLog.Write(ex, $"Reading attachments in {index.Root}");
                    }
                }

                if (changed)
                {
                    _dispatcher.BeginInvoke(() => IndexUpdated?.Invoke());
                }
            }

            WaitHandle.WaitAny([_attachmentSignal, token.WaitHandle], TimeSpan.FromMinutes(10));
        }
    }

    private void StopAttachmentIndexing() => _attachmentStop.Cancel();
}
