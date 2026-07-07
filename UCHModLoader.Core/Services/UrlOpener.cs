using System.Diagnostics;

namespace UCHModLoader.Core.Services;

/// <summary>
/// Opens URLs (http, steam://, …) with the platform's default handler.
/// Windows shell-executes; Linux uses xdg-open; macOS uses open.
/// </summary>
public static class UrlOpener
{
    public static void Open(string url)
    {
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