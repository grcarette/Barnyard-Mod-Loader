using System.Diagnostics;

namespace UCHModLoader.Core.Services;

/// <summary>
/// Opens URLs with the platform's default handler. Windows shell-executes;
/// Linux uses xdg-open; macOS uses open. Only web and Steam URLs are ever
/// opened: some values come from the server (e.g. the loader update link),
/// and shell-executing an arbitrary string (file://, UNC paths, custom
/// protocol handlers) would let a compromised server run things locally.
/// </summary>
public static class UrlOpener
{
    private static readonly string[] AllowedSchemes = { "http", "https", "steam" };

    public static void Open(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (!AllowedSchemes.Contains(uri.Scheme.ToLowerInvariant())) return;

        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        else if (OperatingSystem.IsMacOS())
        {
            Process.Start("open", url);
        }
        else
        {
            Process.Start("xdg-open", url);
        }
    }
}