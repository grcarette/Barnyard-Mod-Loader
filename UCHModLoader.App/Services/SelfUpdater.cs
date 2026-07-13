using System.Diagnostics;
using System.IO.Compression;

namespace UCHModLoader.App.Services;

/// <summary>
/// In-place self-update for the portable single-file build (Windows).
///
/// A running exe (and its loaded native libraries) can't be overwritten, but
/// they CAN be renamed — so the update downloads the release zip, renames any
/// locked file aside as "*.old", writes the new files where they were, and
/// relaunches. ReleaseJunkCleanup sweeps the .old files on the next start.
/// </summary>
public static class SelfUpdater
{
    private static readonly HttpClient Http = new();

    /// <summary>Auto-update requires Windows, https, and a direct zip.</summary>
    public static bool CanAutoUpdate(string? zipUrl) =>
        OperatingSystem.IsWindows() &&
        !string.IsNullOrEmpty(zipUrl) &&
        zipUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
        zipUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Downloads and applies the update into the app's own folder.
    /// Throws on failure; on success the app should restart via
    /// <see cref="RestartIntoNewVersion"/>.
    /// </summary>
    public static async Task ApplyAsync(string zipUrl, IProgress<double>? progress = null)
    {
        // Single-file apps load framework assemblies lazily out of the exe's
        // own bundle. Once the exe is renamed aside, anything not yet loaded
        // (System.Diagnostics.Process for the restart) can no longer be found
        // — so force-load and JIT the restart path before touching any files.
        System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(
            typeof(SelfUpdater).GetMethod(nameof(RestartIntoNewVersion))!.MethodHandle);
        _ = new ProcessStartInfo();

        var baseDir = Path.GetFullPath(AppContext.BaseDirectory);
        var tempZip = Path.Combine(Path.GetTempPath(), $"barnyard-update-{Guid.NewGuid():N}.zip");

        try
        {
            await DownloadAsync(zipUrl, tempZip, progress);

            using var archive = ZipFile.OpenRead(tempZip);
            var rootPrefix = baseDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;   // directory entry

                var destination = Path.GetFullPath(Path.Combine(baseDir, entry.FullName));
                if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Update entry '{entry.FullName}' escapes the app folder.");

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                MoveAsideIfLocked(destination);
                entry.ExtractToFile(destination, overwrite: true);
            }
        }
        finally
        {
            try { File.Delete(tempZip); } catch { }
        }
    }

    /// <summary>Launches the (now updated) exe; the caller shuts this instance down.</summary>
    public static void RestartIntoNewVersion()
    {
        var exe = Environment.ProcessPath
                  ?? Path.Combine(AppContext.BaseDirectory, "Barnyard.exe");
        Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
    }

    /// <summary>Deletes leftover "*.old" files from a previous update. Call at startup.</summary>
    public static void CleanupOldFiles()
    {
        try
        {
            foreach (var old in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.old"))
            {
                try { File.Delete(old); } catch { /* still locked — next start */ }
            }
        }
        catch { }
    }

    private static async Task DownloadAsync(string url, string destination, IProgress<double>? progress)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1;
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var file = File.Create(destination);

        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read));
            written += read;
            if (total > 0) progress?.Report(100.0 * written / total);
        }
    }

    // Overwriting fails for the running exe and loaded native dlls; renaming
    // works. Try the plain delete first (most files), fall back to rename.
    private static void MoveAsideIfLocked(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            var aside = path + ".old";
            try { File.Delete(aside); } catch { }
            if (File.Exists(aside)) aside = path + "." + Guid.NewGuid().ToString("N")[..8] + ".old";
            File.Move(path, aside);
        }
        catch (UnauthorizedAccessException)
        {
            var aside = path + ".old";
            try { File.Delete(aside); } catch { }
            if (File.Exists(aside)) aside = path + "." + Guid.NewGuid().ToString("N")[..8] + ".old";
            File.Move(path, aside);
        }
    }
}
