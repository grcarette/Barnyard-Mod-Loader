using Avalonia.Media.Imaging;

namespace UCHModLoader.App.Services;

/// <summary>
/// Disk cache for mod icons at %AppData%/UCHModLoader/iconcache/.
/// Files are keyed by mod id + icon version (the server's icon file id),
/// so a re-uploaded icon gets a new version and invalidates naturally.
/// </summary>
public sealed class IconCache
{
    private static readonly HttpClient Http = new();
    private readonly string _cacheDirectory;

    public IconCache(string? cacheDirectory = null)
    {
        _cacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UCHModLoader", "iconcache");
        Directory.CreateDirectory(_cacheDirectory);
    }

    /// <summary>
    /// Returns the icon bitmap, from disk when cached, otherwise downloading
    /// and caching it. Returns null when there is no icon or on failure.
    /// </summary>
    public async Task<Bitmap?> GetAsync(string modId, string? iconVersion, string? iconUrl)
    {
        if (string.IsNullOrEmpty(iconUrl)) return null;

        var version = string.IsNullOrEmpty(iconVersion) ? "0" : iconVersion;
        var cachePath = Path.Combine(_cacheDirectory, $"{SafeKey(modId)}-{SafeKey(version)}.png");

        try
        {
            if (File.Exists(cachePath))
                return new Bitmap(cachePath);
        }
        catch
        {
            // Corrupt cache file: fall through to re-download.
            try { File.Delete(cachePath); } catch { }
        }

        try
        {
            var bytes = await Http.GetByteArrayAsync(iconUrl);

            PruneOldVersions(modId, cachePath);
            await File.WriteAllBytesAsync(cachePath, bytes);

            return new Bitmap(new MemoryStream(bytes));
        }
        catch
        {
            return null; // offline or missing icon — not an error
        }
    }

    /// <summary>Path of the cached icon file for this mod+version, or null if not cached yet.</summary>
    public string? TryGetCachedPath(string modId, string? iconVersion)
    {
        var version = string.IsNullOrEmpty(iconVersion) ? "0" : iconVersion;
        var cachePath = Path.Combine(_cacheDirectory, $"{SafeKey(modId)}-{SafeKey(version)}.png");
        return File.Exists(cachePath) ? cachePath : null;
    }

    private void PruneOldVersions(string modId, string keepPath)
    {
        try
        {
            foreach (var file in Directory.GetFiles(_cacheDirectory, $"{SafeKey(modId)}-*.png"))
            {
                if (!string.Equals(file, keepPath, StringComparison.OrdinalIgnoreCase))
                    File.Delete(file);
            }
        }
        catch { }
    }

    private static string SafeKey(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}