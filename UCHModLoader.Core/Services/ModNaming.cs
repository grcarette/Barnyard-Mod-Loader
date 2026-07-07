namespace UCHModLoader.Core.Services;

/// <summary>
/// Shared rules for turning a mod's display name into a safe folder name.
/// Used by the loader (install folder) and the server (uniqueness check),
/// so the two can never disagree.
/// </summary>
public static class ModNaming
{
    public const int MaxNameLength = 60;

    /// <summary>Turns a display name into a filesystem-safe folder name.</summary>
    public static string ToFolderName(string displayName, string fallback)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return fallback;

        var invalid = Path.GetInvalidFileNameChars();
        var chars = displayName.Trim()
            .Select(c => invalid.Contains(c) ? '_' : c)
            .ToArray();

        var cleaned = new string(chars).TrimEnd('.', ' ').Trim();

        if (cleaned.Length == 0) return fallback;
        if (cleaned.Length > MaxNameLength) cleaned = cleaned[..MaxNameLength].TrimEnd('.', ' ');

        // Windows reserved device names can't be folder names.
        var reserved = new[] { "CON", "PRN", "AUX", "NUL",
            "COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
            "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9" };
        if (reserved.Contains(cleaned.ToUpperInvariant())) return fallback;

        return cleaned;
    }

    /// <summary>Validates a display name for upload. Returns an error message or null if valid.</summary>
    public static string? Validate(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return "Mod name is required.";
        if (displayName.Trim().Length > MaxNameLength)
            return $"Mod name must be {MaxNameLength} characters or fewer.";
        return null;
    }
}