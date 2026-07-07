using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using UCHModLoader.Core.Models;

namespace UCHModLoader.Core.Services;

public sealed class GitHubModRepository : IModRepository
{
    private readonly HttpClient _http;
    private readonly string _indexUrl;
    private readonly string? _packsUrl;
    private readonly Func<string?>? _tokenProvider;

    public GitHubModRepository(HttpClient http, string indexUrl, string? packsUrl = null,
        Func<string?>? tokenProvider = null)
    {
        _http = http;
        _indexUrl = indexUrl;
        _packsUrl = packsUrl;
        _tokenProvider = tokenProvider;
    }

    private HttpRequestMessage Request(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        var token = _tokenProvider?.Invoke();
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    public async Task<ModIndex> GetIndexAsync(CancellationToken ct = default)
    {
        using var request = Request(_indexUrl);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var index = await JsonSerializer.DeserializeAsync<ModIndex>(stream, cancellationToken: ct)
                    ?? throw new InvalidDataException("Mod index was empty or invalid.");
        if (index.SchemaVersion > 1)
            throw new NotSupportedException(
                $"Mod index schema v{index.SchemaVersion} requires a newer loader. Please update.");
        return index;
    }

    public async Task<IReadOnlyList<ModPack>> GetPacksAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_packsUrl)) return Array.Empty<ModPack>();
        try
        {
            using var request = Request(_packsUrl);
            using var response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync<List<ModPack>>(stream, cancellationToken: ct)
                   ?? new List<ModPack>();
        }
        catch
        {
            return Array.Empty<ModPack>(); // packs are optional decoration
        }
    }

    public async Task<Stream> DownloadAsync(ModVersionInfo version, CancellationToken ct = default)
    {
        using var request = Request(version.DownloadUrl);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return new MemoryStream(bytes);
    }
}