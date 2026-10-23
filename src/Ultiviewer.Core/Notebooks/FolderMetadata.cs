using System.Text.Json;
using System.Text.Json.Serialization;
using Ultiviewer.Core.Files;

namespace Ultiviewer.Core.Notebooks;

/// <summary>Contents of the per-folder <c>.ultiviewer.json</c> sidecar.</summary>
public sealed record FolderMetadata
{
    public static FolderMetadata Empty { get; } = new();

    public int Version { get; init; } = 1;

    /// <summary>Entry names (<c>Page.md</c>, <c>Section</c>, <c>file.txt</c>) in manual order.</summary>
    public IReadOnlyList<string> Order { get; init; } = [];

    /// <summary>Section or notebook color as <c>#RRGGBB</c>.</summary>
    public string? Color { get; init; }

    [JsonIgnore]
    public bool IsEmpty => Order.Count == 0 && Color is null;
}

public static class FolderMetadataStore
{
    public const string FileName = ".ultiviewer.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>Reads the sidecar. A missing or unreadable sidecar means "no metadata".</summary>
    public static FolderMetadata Read(string folder)
    {
        var path = Path.Combine(folder, FileName);
        try
        {
            if (!File.Exists(path))
            {
                return FolderMetadata.Empty;
            }

            var metadata = JsonSerializer.Deserialize<FolderMetadata>(File.ReadAllBytes(path), Options);
            return metadata is null
                ? FolderMetadata.Empty
                : metadata with { Order = metadata.Order?.Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? [] };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return FolderMetadata.Empty;
        }
    }

    /// <summary>Writes the sidecar, or deletes it when the metadata is empty.</summary>
    public static void Write(string folder, FolderMetadata metadata)
    {
        var path = Path.Combine(folder, FileName);
        if (metadata.IsEmpty)
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }

            return;
        }

        AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(metadata, Options));
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
    }

    /// <summary>Applies <paramref name="mutate"/> to the stored order and saves it if it reports a change.</summary>
    public static void UpdateOrder(string folder, Func<List<string>, bool> mutate)
    {
        var metadata = Read(folder);
        var order = metadata.Order.ToList();
        if (mutate(order))
        {
            Write(folder, metadata with { Order = order });
        }
    }
}
