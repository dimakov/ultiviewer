using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ultiviewer.Core.Settings;

namespace Ultiviewer.App.Services;

/// <summary>
/// Keeps one Ultiviewer per user (and per state folder): a second launch hands its file arguments to the
/// running instance over a named pipe and exits, so double-clicking files in Explorer opens tabs.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly string _name;
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(string name, Mutex mutex)
    {
        _name = name;
        _mutex = mutex;
    }

    /// <summary>Returns the owner when this is the first instance; otherwise forwards the files and returns null.</summary>
    public static SingleInstance? Acquire(IReadOnlyList<string> files)
    {
        // Distinct per user and per state folder, so ULTIVIEWER_HOME test runs don't hand files to a real instance.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName + "|" + AppPaths.LocalDirectory.ToUpperInvariant())))[..16];
        var name = "Ultiviewer-" + key;
        var mutex = new Mutex(initiallyOwned: true, @"Local\" + name, out var createdNew);
        if (createdNew)
        {
            return new SingleInstance(name, mutex);
        }

        mutex.Dispose();
        try
        {
            AllowSetForegroundWindow(-1); // let the running instance come to the front
            using var client = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            var payload = JsonSerializer.SerializeToUtf8Bytes(files.Select(Path.GetFullPath).ToArray());
            client.Write(payload);
            return null;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            // The other instance is stuck or closing: run anyway rather than doing nothing.
            ErrorLog.Write(ex, "Forwarding files to the running instance");
            return new SingleInstance(name, new Mutex(false));
        }
    }

    /// <summary>Listens for files from later launches; <paramref name="onFiles"/> runs on a background thread.</summary>
    public void Listen(Action<string[]> onFiles) => _ = Task.Run(async () =>
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token);
                using var buffer = new MemoryStream();
                await server.CopyToAsync(buffer, _stop.Token);
                var files = buffer.Length == 0 ? [] : JsonSerializer.Deserialize<string[]>(buffer.ToArray()) ?? [];
                onFiles(files);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                ErrorLog.Write(ex, "Receiving files from another launch");
            }
        }
    });

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }

        _mutex.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
