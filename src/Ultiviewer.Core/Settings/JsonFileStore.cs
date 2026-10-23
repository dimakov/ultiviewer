using System.Text.Json;
using System.Text.Json.Serialization;
using Ultiviewer.Core.Files;

namespace Ultiviewer.Core.Settings;

/// <summary>Loads and saves JSON state files. A missing or corrupt file yields a fresh object.</summary>
public static class JsonFileStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static T Load<T>(string path) where T : new()
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Options) ?? new T();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Fall through to defaults; the next save replaces the bad file.
        }

        return new T();
    }

    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, Options));
    }
}
