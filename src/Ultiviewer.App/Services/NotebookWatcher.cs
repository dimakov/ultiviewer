using System.Collections.Concurrent;
using System.IO;
using Ultiviewer.Core.Notebooks;

namespace Ultiviewer.App.Services;

/// <summary>
/// Watches a notebook folder. <see cref="StructureChanged"/> fires (debounced) when files or folders are
/// added, removed or renamed, or a sidecar changes; <see cref="FileChanged"/> fires (debounced per file) when
/// a file's content changes, it is created, deleted or renamed. Events arrive on a thread-pool thread.
/// </summary>
public sealed class NotebookWatcher : IDisposable
{
    private static readonly TimeSpan StructureDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan FileDelay = TimeSpan.FromMilliseconds(250);

    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _structureTimer;
    private readonly ConcurrentDictionary<string, Timer> _fileTimers = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public NotebookWatcher(string root)
    {
        _structureTimer = new Timer(_ => StructureChanged?.Invoke());
        if (!Directory.Exists(root))
        {
            return;
        }

        _watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Created += (_, e) => OnEntry(e.FullPath, structural: true);
        _watcher.Deleted += (_, e) => OnEntry(e.FullPath, structural: true);
        _watcher.Changed += (_, e) => OnEntry(e.FullPath, structural: false);
        _watcher.Renamed += (_, e) =>
        {
            OnEntry(e.OldFullPath, structural: true);
            OnEntry(e.FullPath, structural: true);
        };
        _watcher.Error += (_, _) =>
        {
            // Buffer overflow: everything may have changed.
            ScheduleStructure();
            FullRescanRequested?.Invoke();
        };
        _watcher.EnableRaisingEvents = true;
    }

    public event Action? StructureChanged;

    public event Action<string>? FileChanged;

    /// <summary>Events were lost; consumers should re-check all files.</summary>
    public event Action? FullRescanRequested;

    private void OnEntry(string path, bool structural)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(NotebookScanner.TempSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (structural || name.Equals(FolderMetadataStore.FileName, StringComparison.OrdinalIgnoreCase))
        {
            ScheduleStructure();
        }

        if (!name.Equals(FolderMetadataStore.FileName, StringComparison.OrdinalIgnoreCase))
        {
            ScheduleFile(path);
        }
    }

    private void ScheduleStructure()
    {
        if (!_disposed)
        {
            _structureTimer.Change(StructureDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void ScheduleFile(string path)
    {
        if (_disposed)
        {
            return;
        }

        var timer = _fileTimers.GetOrAdd(path, p => new Timer(_ =>
        {
            if (_fileTimers.TryRemove(p, out var t))
            {
                t.Dispose();
            }

            FileChanged?.Invoke(p);
        }));
        timer.Change(FileDelay, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        _disposed = true;
        _watcher?.Dispose();
        _structureTimer.Dispose();
        foreach (var timer in _fileTimers.Values)
        {
            timer.Dispose();
        }
    }
}
