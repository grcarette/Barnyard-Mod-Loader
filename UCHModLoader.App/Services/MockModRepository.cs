using UCHModLoader.Core;
using UCHModLoader.Core.Models;

namespace UCHModLoader.App.Services;

/// <summary>
/// Fake repository so the app is fully usable before a real index exists.
/// Downloads return an empty zip, so installs "succeed" without real files.
/// </summary>
public sealed class MockModRepository : IModRepository
{
    public Task<ModIndex> GetIndexAsync(CancellationToken ct = default)
    {
        var index = new ModIndex
        {
            Mods =
            {
                new ModEntry
                {
                    Id = "UCHLib", Name = "UCHLib", Author = "modteam",
                    Description = "Shared library used by other mods",
                    Versions =
                    {
                        new ModVersionInfo { Version = "1.2.1", DownloadUrl = "mock://UCHLib/1.2.1" },
                        new ModVersionInfo { Version = "1.3.0", DownloadUrl = "mock://UCHLib/1.3.0" },
                    }
                },
                new ModEntry
                {
                    Id = "CustomLevelsPlus", Name = "Custom Levels Plus", Author = "henlo",
                    Description = "Adds extended level editor pieces",
                    Versions =
                    {
                        new ModVersionInfo
                        {
                            Version = "1.4.0", DownloadUrl = "mock://CustomLevelsPlus/1.4.0",
                            Dependencies = { ["UCHLib"] = ">=1.2.0" }
                        },
                    }
                },
                new ModEntry
                {
                    Id = "ChaosMode", Name = "Chaos Mode", Author = "glitchwitch",
                    Description = "Randomized traps every round",
                    Versions =
                    {
                        new ModVersionInfo
                        {
                            Version = "0.3.0", DownloadUrl = "mock://ChaosMode/0.3.0",
                            Dependencies = { ["UCHLib"] = ">=1.3.0" }
                        },
                    }
                },
            }
        };
        return Task.FromResult(index);
    }

    public Task<IReadOnlyList<ModPack>> GetPacksAsync(CancellationToken ct = default)
    {
        IReadOnlyList<ModPack> packs = new List<ModPack>
        {
            new ModPack
            {
                Id = "mock-pack-1",
                Name = "Party Starter",
                Description = "Everything you need for chaotic group sessions",
                ModIds = { "ChaosMode", "CustomLevelsPlus" },
            },
        };
        return Task.FromResult(packs);
    }

    public Task<Stream> DownloadAsync(ModVersionInfo version, CancellationToken ct = default)
    {
        var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("placeholder.dll");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("mock");
        }
        ms.Position = 0;
        return Task.FromResult<Stream>(ms);
    }
}