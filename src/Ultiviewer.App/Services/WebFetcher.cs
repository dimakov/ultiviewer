using System.IO;
using System.Net;
using System.Net.Http;

namespace Ultiviewer.App.Services;

/// <summary>A downloaded page: its bytes, the charset the server named (if any) and the final address after redirects.</summary>
public sealed record FetchedPage(byte[] Content, string? Charset, Uri Url);

/// <summary>Downloads web pages and pictures for "Clip web page…" (also reads local .html files), with size limits.</summary>
public sealed class WebFetcher : IDisposable
{
    private const long MaxPageBytes = 10 * 1024 * 1024;
    private const long MaxImageBytes = 20 * 1024 * 1024;

    private readonly HttpClient _http;

    public WebFetcher()
    {
        _http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, MaxAutomaticRedirections = 10 })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Mozilla/5.0 (Windows NT 10.0; Win64; x64) Ultiviewer/{typeof(WebFetcher).Assembly.GetName().Version?.ToString(3)}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml;q=0.9,*/*;q=0.8");
    }

    public async Task<FetchedPage> FetchPageAsync(Uri address, CancellationToken cancellationToken = default)
    {
        if (address.IsFile)
        {
            return new FetchedPage(await ReadFileAsync(address.LocalPath, MaxPageBytes, cancellationToken), null, address);
        }

        using var response = await _http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var type = response.Content.Headers.ContentType?.MediaType;
        if (type is not null && !type.Contains("html", StringComparison.OrdinalIgnoreCase) && !type.Contains("xml", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The address is not a web page ({type}).");
        }

        var bytes = await ReadLimitedAsync(response, MaxPageBytes, cancellationToken);
        return new FetchedPage(bytes, response.Content.Headers.ContentType?.CharSet, response.RequestMessage?.RequestUri ?? address);
    }

    public async Task<byte[]> FetchBytesAsync(Uri source, CancellationToken cancellationToken = default)
    {
        if (source.IsFile)
        {
            return await ReadFileAsync(source.LocalPath, MaxImageBytes, cancellationToken);
        }

        using var response = await _http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await ReadLimitedAsync(response, MaxImageBytes, cancellationToken);
    }

    private static async Task<byte[]> ReadFileAsync(string path, long max, CancellationToken cancellationToken) =>
        new FileInfo(path).Length > max
            ? throw new InvalidDataException($"{Path.GetFileName(path)} is too large.")
            : await File.ReadAllBytesAsync(path, cancellationToken);

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, long max, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > max)
        {
            throw new InvalidDataException("The download is too large.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > max)
            {
                throw new InvalidDataException("The download is too large.");
            }
        }

        return buffer.ToArray();
    }

    public void Dispose() => _http.Dispose();
}
