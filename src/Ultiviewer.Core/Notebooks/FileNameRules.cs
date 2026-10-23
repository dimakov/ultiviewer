namespace Ultiviewer.Core.Notebooks;

/// <summary>Rules for page, section and file names, so every name is a valid Windows file name.</summary>
public static class FileNameRules
{
    public const int MaxLength = 200;

    private static readonly char[] InvalidChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Returns a user-facing error, or null when <paramref name="name"/> is valid.</summary>
    public static string? Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "The name can't be empty.";
        }

        if (name.Length > MaxLength)
        {
            return $"The name can't be longer than {MaxLength} characters.";
        }

        var bad = name.FirstOrDefault(c => c < 32 || InvalidChars.Contains(c));
        if (bad != default)
        {
            return bad < 32
                ? "The name can't contain control characters."
                : $"The name can't contain {string.Join(" ", InvalidChars)}";
        }

        if (name.EndsWith(' ') || name.EndsWith('.'))
        {
            return "The name can't end with a space or a period.";
        }

        if (name.StartsWith(' '))
        {
            return "The name can't start with a space.";
        }

        if (name.StartsWith('.'))
        {
            return "Names starting with a period are hidden, so they aren't allowed.";
        }

        if (name.EndsWith(NotebookScanner.AssetsSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return $"Names ending with {NotebookScanner.AssetsSuffix} are reserved for attachments.";
        }

        var stem = name.Split('.')[0];
        if (ReservedNames.Contains(stem))
        {
            return $"{stem} is a reserved name in Windows.";
        }

        return null;
    }

    public static void EnsureValid(string name)
    {
        if (Validate(name) is { } error)
        {
            throw new NotebookOperationException(error);
        }
    }
}
