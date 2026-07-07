using System.Diagnostics;

namespace UCHModLoader.Core.Services;

public sealed class SteamGameLauncher : IGameLauncher
{
    private const string UchAppId = "386940";

    public void Launch()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = $"steam://rungameid/{UchAppId}",
            UseShellExecute = true,
        });
    }
}
