using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.GridFS;
using UCHModLoader.Core.Services;
using UCHModLoader.Server.Services;

using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<MongoContext>();
builder.Services.AddHttpClient();
var app = builder.Build();

var publicBaseUrl = (app.Configuration["PublicBaseUrl"] ?? "http://localhost:5178").TrimEnd('/');

// One-time migration: revisions uploaded before the approval system exist
// without an Approved field — treat them as approved. Idempotent.
{
    using var scope = app.Services.CreateScope();
    var migrationDb = scope.ServiceProvider.GetRequiredService<MongoContext>();
    await migrationDb.Mods.UpdateManyAsync(
        MongoDB.Driver.Builders<ModDoc>.Filter.Empty,
        MongoDB.Driver.Builders<ModDoc>.Update.Set("Versions.$[v].Approved", true),
        new UpdateOptions
        {
            ArrayFilters = new[]
            {
                new BsonDocumentArrayFilterDefinition<BsonDocument>(
                    new BsonDocument("v.Approved", new BsonDocument("$exists", false))),
            },
        });
}

// Strict Authorization-header parsing: exactly "Bearer <token>" or nothing.
static string? BearerToken(HttpRequest request)
{
    var header = request.Headers.Authorization.ToString();
    return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? header["Bearer ".Length..].Trim()
        : null;
}

// Rate limiting: sliding window per action+user, in memory (single instance).
var actionTimestamps = new ConcurrentDictionary<string, Queue<DateTime>>();
bool AllowAction(string key, int max, TimeSpan window)
{
    var queue = actionTimestamps.GetOrAdd(key, _ => new Queue<DateTime>());
    lock (queue)
    {
        var cutoff = DateTime.UtcNow - window;
        while (queue.Count > 0 && queue.Peek() < cutoff) queue.Dequeue();
        if (queue.Count >= max) return false;
        queue.Enqueue(DateTime.UtcNow);
        return true;
    }
}
bool AllowUpload(string discordId) => AllowAction("upload:" + discordId, 10, TimeSpan.FromHours(1));

// OAuth state: server-issued nonces (carrying the loader's loopback port)
// that the callback must present, so a login can't be forged or replayed.
var oauthStates = new ConcurrentDictionary<string, (string Port, DateTime Expires)>();

// ---------- Index ----------
app.MapGet("/api/index", async (HttpRequest request, IConfiguration config, MongoContext db) =>
{
    // Identity is optional here: anonymous callers get public mods; a valid
    // token additionally reveals private mods the caller may access.
    var token = BearerToken(request);
    var user = await db.UserFromTokenAsync(token);
    var admins = config.GetSection("Admin:DiscordIds").Get<string[]>() ?? Array.Empty<string>();
    var isAdmin = user is not null && admins.Contains(user.DiscordId);

    var allMods = await db.Mods.Find(m => !m.Hidden).ToListAsync();
    var mods = allMods.Where(m =>
        !m.IsPrivate ||
        isAdmin ||
        (user is not null &&
         (m.OwnerDiscordId == user.DiscordId ||
          m.AuthorizedDiscordIds.Contains(user.DiscordId)))).ToList();

    // Approval: the public sees approved revisions only; owners and admins
    // also see pending ones (owners can test their own pending builds).
    var visible = new List<(ModDoc Mod, List<ModVersionDoc> Versions)>();
    foreach (var m in mods)
    {
        var canSeePending = isAdmin ||
            (user is not null && m.OwnerDiscordId == user.DiscordId);
        var versions = m.Versions
            .Where(v => v.Approved || canSeePending)
            .ToList();
        if (versions.Count > 0) visible.Add((m, versions));
    }

    var ownerIds = visible.Select(x => x.Mod.OwnerDiscordId).Distinct().ToList();
    var owners = await db.Users.Find(u => ownerIds.Contains(u.DiscordId)).ToListAsync();
    var verifiedOwners = owners.Where(u => u.Verified).Select(u => u.DiscordId)
        .ToHashSet(StringComparer.Ordinal);
    var index = new
    {
        schemaVersion = 1,
        mods = visible.Select(x => new
        {
            id = x.Mod.ModId,
            name = x.Mod.Name,
            author = x.Mod.Author,
            description = x.Mod.Description,
            iconUrl = x.Mod.IconFileId is null ? null : $"{publicBaseUrl}/api/icon/{x.Mod.ModId}",
            iconVersion = x.Mod.IconFileId?.ToString(),
            isPrivate = x.Mod.IsPrivate,
            authorVerified = verifiedOwners.Contains(x.Mod.OwnerDiscordId),
            downloads = x.Mod.Downloads,
            upvotes = x.Mod.UpvoterDiscordIds.Count,
            tags = x.Mod.Tags,
            conflicts = x.Mod.Conflicts,
            versions = x.Versions.Select(v => new
            {
                version = v.Version,
                revision = v.Revision,
                approved = v.Approved,
                downloadUrl = $"{publicBaseUrl}/api/download/{x.Mod.ModId}/rev/{v.Revision}",
                sha256 = v.Sha256,
                changelog = v.Changelog,
                uploadedUtc = v.UploadedUtc,
                dependencies = v.Dependencies,
                gameVersion = "*",
            }),
        }),
    };
    return Results.Json(index);
});

// ---------- Packs ----------
app.MapGet("/api/packs", async (MongoContext db) =>
{
    var packs = await db.Packs.Find(p => !p.Hidden).ToListAsync();
    return Results.Json(packs.Select(p => new
    {
        id = p.Id.ToString(),
        name = p.Name,
        description = p.Description,
        modIds = p.ModIds,
        iconUrl = p.IconFileId is null ? null : $"{publicBaseUrl}/api/packicon/{p.Id}",
        iconVersion = p.IconFileId?.ToString(),
    }));
});

// Admin-only: create a pack.
app.MapPost("/api/packs", async (HttpRequest request, IConfiguration config, MongoContext db) =>
{
    var token = BearerToken(request);
    var user = await db.UserFromTokenAsync(token);
    if (user is null) return Results.Unauthorized();

    var admins = config.GetSection("Admin:DiscordIds").Get<string[]>() ?? Array.Empty<string>();
    if (!admins.Contains(user.DiscordId))
        return Results.BadRequest("Only admins can create packs.");

    if (!request.HasFormContentType) return Results.BadRequest("Expected multipart form data.");
    var form = await request.ReadFormAsync();

    var name = form["name"].ToString().Trim();
    if (string.IsNullOrEmpty(name)) return Results.BadRequest("Pack name is required.");
    if (name.Length > 60) return Results.BadRequest("Pack name must be 60 characters or fewer.");

    var description = form["description"].ToString().Trim();

    List<string> modIds;
    try
    {
        modIds = JsonSerializer.Deserialize<List<string>>(form["modIds"].ToString())
                 ?? new List<string>();
    }
    catch { return Results.BadRequest("modIds must be a JSON array of mod GUIDs."); }
    if (modIds.Count == 0) return Results.BadRequest("A pack needs at least one mod.");

    foreach (var modId in modIds)
    {
        var exists = await db.Mods.Find(m => m.ModId == modId).AnyAsync();
        if (!exists) return Results.BadRequest($"Mod '{modId}' does not exist.");
    }

    ObjectId? iconFileId = null;
    var iconFile = form.Files.GetFile("icon");
    if (iconFile is not null && iconFile.Length > 0)
    {
        if (iconFile.Length > 1024 * 1024) return Results.BadRequest("Icon exceeds 1 MB limit.");
        using var iconStream = new MemoryStream();
        await iconFile.OpenReadStream().CopyToAsync(iconStream);
        var normalized = IconProcessor.Normalize(iconStream.ToArray());
        if (normalized is null) return Results.BadRequest("Icon could not be read as an image.");
        iconFileId = await db.Files.UploadFromBytesAsync($"pack-{name}-icon.png", normalized);
    }

    var pack = new PackDoc
    {
        Name = name,
        Description = description,
        ModIds = modIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        IconFileId = iconFileId,
    };
    await db.Packs.InsertOneAsync(pack);

    return Results.Json(new { id = pack.Id.ToString(), name = pack.Name });
});

app.MapGet("/api/packicon/{packId}", async (string packId, MongoContext db) =>
{
    if (!ObjectId.TryParse(packId, out var id)) return Results.NotFound();
    var pack = await db.Packs.Find(p => p.Id == id).FirstOrDefaultAsync();
    if (pack?.IconFileId is null) return Results.NotFound();
    var bytes = await db.Files.DownloadAsBytesAsync(pack.IconFileId.Value);
    return Results.File(bytes, "image/png");
});

// Admin-only: set a pack's icon. Admin Discord IDs come from config
// ("Admin:DiscordIds"), e.g. in user secrets.
app.MapPost("/api/packs/{packId}/icon", async (string packId, HttpRequest request,
    IConfiguration config, MongoContext db) =>
{
    var token = BearerToken(request);
    var user = await db.UserFromTokenAsync(token);
    if (user is null) return Results.Unauthorized();

    var admins = config.GetSection("Admin:DiscordIds").Get<string[]>() ?? Array.Empty<string>();
    if (!admins.Contains(user.DiscordId))
        return Results.BadRequest("Only admins can set pack icons.");

    if (!ObjectId.TryParse(packId, out var id)) return Results.NotFound();
    var pack = await db.Packs.Find(p => p.Id == id).FirstOrDefaultAsync();
    if (pack is null) return Results.NotFound();

    if (!request.HasFormContentType) return Results.BadRequest("Expected multipart form data.");
    var form = await request.ReadFormAsync();
    var iconFile = form.Files.GetFile("icon");
    if (iconFile is null || iconFile.Length == 0) return Results.BadRequest("icon file is required.");
    if (iconFile.Length > 1024 * 1024) return Results.BadRequest("Icon exceeds 1 MB limit.");

    using var iconStream = new MemoryStream();
    await iconFile.OpenReadStream().CopyToAsync(iconStream);
    var normalized = IconProcessor.Normalize(iconStream.ToArray());
    if (normalized is null) return Results.BadRequest("Icon could not be read as an image.");

    var newIconId = await db.Files.UploadFromBytesAsync($"pack-{packId}-icon.png", normalized);
    await db.Packs.UpdateOneAsync(p => p.Id == id,
        Builders<PackDoc>.Update.Set(p => p.IconFileId, newIconId));

    if (pack.IconFileId is not null)
    {
        try { await db.Files.DeleteAsync(pack.IconFileId.Value); } catch { }
    }

    return Results.Json(new { packId, updated = true });
});

// ---------- Download ----------
app.MapGet("/api/loaderversion", (IConfiguration config) => Results.Json(new
{
    version = config["Loader:Version"] ?? "1.0.0",
    downloadUrl = config["Loader:DownloadUrl"] ?? "",
}));

app.MapGet("/api/download/{modId}/rev/{revision:int}", async (string modId, int revision,
    HttpRequest request, IConfiguration config, MongoContext db) =>
{
    var mod = await db.Mods.Find(m => m.ModId == modId && !m.Hidden).FirstOrDefaultAsync();
    var ver = mod?.Versions.FirstOrDefault(v => v.Revision == revision);
    if (ver is null) return Results.NotFound();

    if (!ver.Approved)
    {
        var pendingToken = request.Headers.Authorization.ToString().Replace("Bearer ", "");
        var pendingUser = await db.UserFromTokenAsync(pendingToken);
        var pendingAdmins = config.GetSection("Admin:DiscordIds").Get<string[]>() ?? Array.Empty<string>();
        var pendingAllowed = pendingUser is not null &&
            (pendingAdmins.Contains(pendingUser.DiscordId) ||
             mod!.OwnerDiscordId == pendingUser.DiscordId);
        if (!pendingAllowed) return Results.NotFound();
    }

    if (mod!.IsPrivate)
    {
        var token = BearerToken(request);
        var user = await db.UserFromTokenAsync(token);
        var admins = config.GetSection("Admin:DiscordIds").Get<string[]>() ?? Array.Empty<string>();
        var allowed = user is not null &&
            (admins.Contains(user.DiscordId) ||
             mod.OwnerDiscordId == user.DiscordId ||
             mod.AuthorizedDiscordIds.Contains(user.DiscordId));
        if (!allowed) return Results.NotFound();
    }

    var bytes = await db.Files.DownloadAsBytesAsync(ver.ZipFileId);
    await db.Mods.UpdateOneAsync(m => m.ModId == modId,
        Builders<ModDoc>.Update.Inc(m => m.Downloads, 1));
    return Results.File(bytes, "application/zip", $"{modId}-r{revision}.zip");
});

app.MapGet("/api/download/{modId}/{version}", async (string modId, string version,
    HttpRequest request, IConfiguration config, MongoContext db) =>
{
    var mod = await db.Mods.Find(m => m.ModId == modId && !m.Hidden).FirstOrDefaultAsync();
    var ver = mod?.Versions.FirstOrDefault(v => v.Version == version);
    if (ver is null) return Results.NotFound();

    if (mod!.IsPrivate)
    {
        var token = BearerToken(request);
        var user = await db.UserFromTokenAsync(token);
        var admins = config.GetSection("Admin:DiscordIds").Get<string[]>() ?? Array.Empty<string>();
        var allowed = user is not null &&
            (admins.Contains(user.DiscordId) ||
             mod.OwnerDiscordId == user.DiscordId ||
             mod.AuthorizedDiscordIds.Contains(user.DiscordId));
        // NotFound (not 403) so unauthorized callers can't confirm the mod exists.
        if (!allowed) return Results.NotFound();
    }
    var bytes = await db.Files.DownloadAsBytesAsync(ver.ZipFileId);

    await db.Mods.UpdateOneAsync(m => m.ModId == modId,
        Builders<ModDoc>.Update.Inc(m => m.Downloads, 1));

    return Results.File(bytes, "application/zip", $"{modId}-{version}.zip");
});

// ---------- Private mod keys ----------
app.MapPost("/api/mods/{modId}/generatekey", async (string modId, HttpRequest request,
    IConfiguration config, MongoContext db) =>
{
    var token = BearerToken(request);
    var user = await db.UserFromTokenAsync(token);
    if (user is null) return Results.Unauthorized();

    var admins = config.GetSection("Admin:DiscordIds").Get<string[]>() ?? Array.Empty<string>();

    if (!AllowAction("genkey:" + user.DiscordId, 30, TimeSpan.FromHours(1)))
        return Results.BadRequest("Too many keys generated — try again later.");

    var mod = await db.Mods.Find(m => m.ModId == modId).FirstOrDefaultAsync();
    if (mod is null) return Results.NotFound();
    if (mod.OwnerDiscordId != user.DiscordId && !admins.Contains(user.DiscordId))
        return Results.BadRequest("You do not own this mod.");
    if (!mod.IsPrivate)
        return Results.BadRequest("This mod is public — keys are only for private mods.");

    var multiUse = false;
    if (request.HasFormContentType)
    {
        var form = await request.ReadFormAsync();
        multiUse = form["multiUse"] == "true";
    }

    var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
    var key = $"{raw[..4]}-{raw[4..8]}-{raw[8..12]}-{raw[12..]}";

    if (multiUse)
    {
        var expiresUtc = DateTime.UtcNow.AddHours(48);
        await db.Mods.UpdateOneAsync(m => m.ModId == modId,
            Builders<ModDoc>.Update.Push(m => m.MultiUseKeys,
                new MultiUseKey { Key = key, ExpiresUtc = expiresUtc }));
        return Results.Json(new { modId, key, expiresUtc });
    }

    await db.Mods.UpdateOneAsync(m => m.ModId == modId,
        Builders<ModDoc>.Update.AddToSet(m => m.PrivateKeys, key));

    return Results.Json(new { modId, key });
});

app.MapPost("/api/mods/redeemkey", async (HttpRequest request, MongoContext db) =>
{
    var token = BearerToken(request);
    var user = await db.UserFromTokenAsync(token);
    if (user is null) return Results.Unauthorized();

    if (!AllowAction("redeem:" + user.DiscordId, 10, TimeSpan.FromMinutes(1)))
        return Results.BadRequest("Too many attempts — try again in a minute.");

    if (!request.HasFormContentType) return Results.BadRequest("Expected form data.");
    var form = await request.ReadFormAsync();
    var key = form["key"].ToString().Trim().ToUpperInvariant();
    if (key.Length == 0) return Results.BadRequest("Key is required.");

    var mod = await db.Mods.Find(
        Builders<ModDoc>.Filter.AnyEq(m => m.PrivateKeys, key)).FirstOrDefaultAsync();
    if (mod is not null)
    {
        // One-time key: consumed on redemption.
        await db.Mods.UpdateOneAsync(m => m.ModId == mod.ModId,
            Builders<ModDoc>.Update
                .Pull(m => m.PrivateKeys, key)
                .AddToSet(m => m.AuthorizedDiscordIds, user.DiscordId));
        return Results.Json(new { modId = mod.ModId, name = mod.Name });
    }

    // Multi-use key: stays valid until it expires.
    mod = await db.Mods.Find(
        Builders<ModDoc>.Filter.ElemMatch(m => m.MultiUseKeys, k => k.Key == key))
        .FirstOrDefaultAsync();
    if (mod is null) return Results.BadRequest("Invalid or already-used key.");

    var entry = mod.MultiUseKeys.First(k => k.Key == key);
    if (entry.ExpiresUtc < DateTime.UtcNow)
    {
        await db.Mods.UpdateOneAsync(m => m.ModId == mod.ModId,
            Builders<ModDoc>.Update.PullFilter(m => m.MultiUseKeys, k => k.Key == key));
        return Results.BadRequest("This key has expired.");
    }

    await db.Mods.UpdateOneAsync(m => m.ModId == mod.ModId,
        Builders<ModDoc>.Update.AddToSet(m => m.AuthorizedDiscordIds, user.DiscordId));

    return Results.Json(new { modId = mod.ModId, name = mod.Name });
});

// ---------- Review (admin) ----------
app.MapGet("/api/admin/pending", async (HttpRequest request, IConfiguration config, MongoContext db) =>
{
    var token = BearerToken(request);
    var user = await db.UserFromTokenAsync(token);
    var admins = config.GetSection("Admin:DiscordIds").Get<string[]>() ?? Array.Empty<string>();
    if (user is null || !admins.Contains(user.DiscordId)) return Results.Unauthorized();

    var mods = await db.Mods.Find(m => m.Versions.Any(v => !v.Approved)).ToListAsync();
    var items = mods.SelectMany(m => m.Versions
        .Where(v => !v.Approved)
        .Select(v => new
        {
            modId = m.ModId,
            name = m.Name,
            author = m.Author,
            ownerDiscordId = m.OwnerDiscordId,
            version = v.Version,
            revision = v.Revision,
            changelog = v.Changelog,
            uploadedUtc = v.UploadedUtc,
            downloadUrl = $"{publicBaseUrl}/api/download/{m.ModId}/rev/{v.Revision}",
        }))
        .OrderBy(i => i.uploadedUtc);
    return Results.Json(items);
});

app.MapPost("/api/admin/approve", async (HttpRequest request, IConfiguration config, MongoContext db) =>
{
    var token = BearerToken(request);
    var user = await db.UserFromTokenAsync(token);
    var admins = config.GetSection("Admin:DiscordIds").Get<string[]>() ?? Array.Empty<string>();
    if (user is null || !admins.Contains(user.DiscordId)) return Results.Unauthorized();

    var form = await request.ReadFormAsync();
    var modId = form["modId"].ToString();
    if (!int.TryParse(form["revision"], out var revision)) return Results.BadRequest("revision required.");
    var verifyAuthor = string.Equals(form["verifyAuthor"], "true", StringComparison.OrdinalIgnoreCase);

    var mod = await db.Mods.Find(m => m.ModId == modId).FirstOrDefaultAsync();
    if (mod is null || mod.Versions.All(v => v.Revision != revision)) return Results.NotFound();

    await db.Mods.UpdateOneAsync(
        Builders<ModDoc>.Filter.Eq(m => m.ModId, modId),
        Builders<ModDoc>.Update.Set("Versions.$[v].Approved", true),
        new UpdateOptions
        {
            ArrayFilters = new[]
            {
                new BsonDocumentArrayFilterDefinition<BsonDocument>(
                    new BsonDocument("v.Revision", revision)),
            },
        });

    if (verifyAuthor)
        await db.Users.UpdateOneAsync(u => u.DiscordId == mod.OwnerDiscordId,
            Builders<UserDoc>.Update.Set(u => u.Verified, true));

    return Results.Json(new { modId, revision, approved = true, authorVerified = verifyAuthor });
});

app.MapPost("/api/admin/reject", async (HttpRequest request, IConfiguration config, MongoContext db) =>
{
    var token = BearerToken(request);
    var user = await db.UserFromTokenAsync(token);
    var admins = config.GetSection("Admin:DiscordIds").Get<string[]>() ?? Array.Empty<string>();
    if (user is null || !admins.Contains(user.DiscordId)) return Results.Unauthorized();

    var form = await request.ReadFormAsync();
    var modId = form["modId"].ToString();
    if (!int.TryParse(form["revision"], out var revision)) return Results.BadRequest("revision required.");

    var mod = await db.Mods.Find(m => m.ModId == modId).FirstOrDefaultAsync();
    var ver = mod?.Versions.FirstOrDefault(v => v.Revision == revision);
    if (mod is null || ver is null) return Results.NotFound();
    if (ver.Approved) return Results.BadRequest("Revision is already approved; hide the mod instead.");

    try { await db.Files.DeleteAsync(ver.ZipFileId); } catch { }

    var remaining = mod.Versions.Where(v => v.Revision != revision).ToList();
    if (remaining.Count == 0)
    {
        if (mod.IconFileId is not null)
        {
            try { await db.Files.DeleteAsync(mod.IconFileId.Value); } catch { }
        }
        await db.Mods.DeleteOneAsync(m => m.ModId == modId);
    }
    else
    {
        await db.Mods.UpdateOneAsync(m => m.ModId == modId,
            Builders<ModDoc>.Update.Set(m => m.Versions, remaining));
    }

    return Results.Json(new { modId, revision, rejected = true });
});

app.MapPost("/api/admin/verify", async (HttpRequest request, IConfiguration config, MongoContext db) =>
{
    var token = BearerToken(request);
    var user = await db.UserFromTokenAsync(token);
    var admins = config.GetSection("Admin:DiscordIds").Get<string[]>() ?? Array.Empty<string>();
    if (user is null || !admins.Contains(user.DiscordId)) return Results.Unauthorized();

    var form = await request.ReadFormAsync();
    var discordId = form["discordId"].ToString();
    var verified = string.Equals(form["verified"], "true", StringComparison.OrdinalIgnoreCase);

    var result = await db.Users.UpdateOneAsync(u => u.DiscordId == discordId,
        Builders<UserDoc>.Update.Set(u => u.Verified, verified));
    if (result.MatchedCount == 0) return Results.NotFound();

    return Results.Json(new { discordId, verified });
});

// ---------- Reporting ----------
app.MapPost("/api/mods/{modId}/report", async (string modId, HttpRequest request, MongoContext db) =>
{
    var token = BearerToken(request);
    var user = await db.UserFromTokenAsync(token);
    if (user is null) return Results.Unauthorized();

    if (!request.HasFormContentType) return Results.BadRequest("Expected form data.");
    var form = await request.ReadFormAsync();
    var reason = form["reason"].ToString().Trim();
    if (reason.Length == 0) return Results.BadRequest("A reason is required.");
    if (reason.Length > 500) reason = reason[..500];

    if (!AllowAction("report:" + user.DiscordId, 5, TimeSpan.FromHours(1)))
        return Results.BadRequest("Too many reports — try again later.");

    var mod = await db.Mods.Find(m => m.ModId == modId).FirstOrDefaultAsync();
    if (mod is null) return Results.NotFound();

    // One report per user per mod: repeat reports can't crowd out others'.
    if (mod.Reports.Any(r => r.ReporterDiscordId == user.DiscordId))
        return Results.Json(new { modId, reported = true });
    if (mod.Reports.Count >= 100)
        return Results.Json(new { modId, reported = true }); // cap; silently accept

    await db.Mods.UpdateOneAsync(m => m.ModId == modId,
        Builders<ModDoc>.Update.Push(m => m.Reports, new ReportEntry
        {
            ReporterDiscordId = user.DiscordId,
            Reason = reason,
            ReportedUtc = DateTime.UtcNow,
        }));

    return Results.Json(new { modId, reported = true });
});

// ---------- Voting ----------
app.MapPost("/api/mods/{modId}/vote", async (string modId, HttpRequest request, MongoContext db) =>
{
    var token = BearerToken(request);
    var user = await db.UserFromTokenAsync(token);
    if (user is null) return Results.Unauthorized();

    if (!AllowAction("vote:" + user.DiscordId, 30, TimeSpan.FromMinutes(1)))
        return Results.BadRequest("Too many votes — slow down.");

    var mod = await db.Mods.Find(m => m.ModId == modId && !m.Hidden).FirstOrDefaultAsync();
    if (mod is null) return Results.NotFound();

    var alreadyVoted = mod.UpvoterDiscordIds.Contains(user.DiscordId);
    var update = alreadyVoted
        ? Builders<ModDoc>.Update.Pull(m => m.UpvoterDiscordIds, user.DiscordId)
        : Builders<ModDoc>.Update.AddToSet(m => m.UpvoterDiscordIds, user.DiscordId);
    await db.Mods.UpdateOneAsync(m => m.ModId == modId, update);

    var upvotes = mod.UpvoterDiscordIds.Count + (alreadyVoted ? -1 : 1);
    return Results.Json(new { modId, upvotes, voted = !alreadyVoted });
});

app.MapGet("/api/votes/mine", async (HttpRequest request, MongoContext db) =>
{
    var token = BearerToken(request);
    var user = await db.UserFromTokenAsync(token);
    if (user is null) return Results.Unauthorized();

    var mods = await db.Mods.Find(m => m.UpvoterDiscordIds.Contains(user.DiscordId)).ToListAsync();
    return Results.Json(mods.Select(m => m.ModId));
});

// ---------- Icon ----------
app.MapGet("/api/icon/{modId}", async (string modId, MongoContext db) =>
{
    var mod = await db.Mods.Find(m => m.ModId == modId).FirstOrDefaultAsync();
    if (mod?.IconFileId is null) return Results.NotFound();
    var bytes = await db.Files.DownloadAsBytesAsync(mod.IconFileId.Value);
    return Results.File(bytes, "image/png");
});

// ---------- Discord OAuth ----------
app.MapGet("/api/auth/discord/login", (IConfiguration config, string? port) =>
{
    // Issue a nonce the callback must return; the loader's loopback port
    // rides inside it server-side instead of in the raw state value.
    foreach (var stale in oauthStates.Where(kv => kv.Value.Expires < DateTime.UtcNow).ToList())
        oauthStates.TryRemove(stale.Key, out _);
    var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    oauthStates[nonce] = (port ?? "", DateTime.UtcNow.AddMinutes(10));

    var clientId = config["Discord:ClientId"];
    var redirect = Uri.EscapeDataString($"{publicBaseUrl}/api/auth/discord/callback");
    return Results.Redirect(
        $"https://discord.com/oauth2/authorize?client_id={clientId}&response_type=code" +
        $"&redirect_uri={redirect}&scope=identify&state={nonce}");
});

app.MapGet("/api/auth/discord/callback", async (string code, string? state, IConfiguration config,
    IHttpClientFactory httpFactory, MongoContext db) =>
{
    // The state must be a nonce we issued (single use, 10-minute lifetime).
    if (state is null || !oauthStates.TryRemove(state, out var stateEntry) ||
        stateEntry.Expires < DateTime.UtcNow)
    {
        return Results.Content(
            "Login session is invalid or expired — start the login again from the loader.",
            "text/plain");
    }

    var http = httpFactory.CreateClient();

    var tokenResponse = await http.PostAsync("https://discord.com/api/oauth2/token",
        new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = config["Discord:ClientId"] ?? "",
            ["client_secret"] = config["Discord:ClientSecret"] ?? "",
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = $"{publicBaseUrl}/api/auth/discord/callback",
        }));
    if (!tokenResponse.IsSuccessStatusCode)
        return Results.Content("Discord login failed. Check client id/secret and redirect URI.", "text/plain");

    using var tokenJson = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
    var accessToken = tokenJson.RootElement.GetProperty("access_token").GetString();

    using var meRequest = new HttpRequestMessage(HttpMethod.Get, "https://discord.com/api/users/@me");
    meRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    using var meJson = JsonDocument.Parse(await (await http.SendAsync(meRequest)).Content.ReadAsStringAsync());
    var discordId = meJson.RootElement.GetProperty("id").GetString() ?? "";
    // Prefer the account-wide display name ("global_name"); accounts that
    // never set one fall back to the unique username.
    var username = meJson.RootElement.TryGetProperty("global_name", out var gn) &&
                   gn.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrWhiteSpace(gn.GetString())
        ? gn.GetString()!
        : meJson.RootElement.GetProperty("username").GetString() ?? "";
    var avatarHash = meJson.RootElement.TryGetProperty("avatar", out var av) ? av.GetString() : null;
    var avatarUrl = avatarHash is null
        ? $"https://cdn.discordapp.com/embed/avatars/{(ulong.TryParse(discordId, out var did) ? (did >> 22) % 6 : 0)}.png"
        : $"https://cdn.discordapp.com/avatars/{discordId}/{avatarHash}.png?size=64";

    var apiToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    await db.Users.UpdateOneAsync(
        u => u.DiscordId == discordId,
        Builders<UserDoc>.Update
            .Set(u => u.Username, username)
            .Set(u => u.AvatarUrl, avatarUrl)
            .Set(u => u.ApiToken, apiToken)
            .SetOnInsert(u => u.CreatedUtc, DateTime.UtcNow),
        new UpdateOptions { IsUpsert = true });

    // Loopback flow: the loader's local listener port rode inside the nonce.
    if (int.TryParse(stateEntry.Port, out var loaderPort) && loaderPort is > 1023 and < 65536)
        return Results.Redirect($"http://127.0.0.1:{loaderPort}/?token={apiToken}");

    // Fallback for logins started outside the loader: show the token to copy.
    var html = $"""
        <html><body style="font-family:sans-serif;max-width:480px;margin:80px auto">
        <h2>Logged in as {username}</h2>
        <p>Paste this token into the loader's Upload tab:</p>
        <p><code style="background:#eee;padding:8px;display:block;word-break:break-all">{apiToken}</code></p>
        <p>Keep it secret — it authorizes uploads under your account.</p>
        </body></html>
        """;
    return Results.Content(html, "text/html");
});

// ---------- Logout: revoke the token server-side ----------
app.MapPost("/api/auth/logout", async (HttpRequest request, MongoContext db) =>
{
    var token = BearerToken(request);
    var user = await db.UserFromTokenAsync(token);
    if (user is null) return Results.Unauthorized();

    await db.Users.UpdateOneAsync(u => u.DiscordId == user.DiscordId,
        Builders<UserDoc>.Update.Set(u => u.ApiToken, ""));
    return Results.Json(new { loggedOut = true });
});

// ---------- Current user ----------
app.MapGet("/api/auth/me", async (HttpRequest request, IConfiguration config, MongoContext db) =>
{
    var token = BearerToken(request);
    var user = await db.UserFromTokenAsync(token);
    if (user is null) return Results.Unauthorized();
    var admins = config.GetSection("Admin:DiscordIds").Get<string[]>() ?? Array.Empty<string>();
    return Results.Json(new
    {
        username = user.Username,
        avatarUrl = user.AvatarUrl,
        isAdmin = admins.Contains(user.DiscordId),
    });
});

// ---------- My mods ----------
app.MapGet("/api/mods/mine", async (HttpRequest request, IConfiguration config, MongoContext db) =>
{
    var token = BearerToken(request);
    var user = await db.UserFromTokenAsync(token);
    if (user is null) return Results.Unauthorized();

    var admins = config.GetSection("Admin:DiscordIds").Get<string[]>() ?? Array.Empty<string>();
    var isAdmin = admins.Contains(user.DiscordId);

    var mods = isAdmin
        ? await db.Mods.Find(_ => true).ToListAsync()
        : await db.Mods.Find(m => m.OwnerDiscordId == user.DiscordId).ToListAsync();
    return Results.Json(mods.Select(m => new
    {
        id = m.ModId,
        name = m.Name,
        version = m.Versions.FirstOrDefault()?.Version ?? "",
        description = m.Description,
        tags = m.Tags,
        isPrivate = m.IsPrivate,
        pendingCount = m.Versions.Count(v => !v.Approved),
    }));
});

// ---------- Update details (icon/description only, no new build) ----------
app.MapPost("/api/mods/{modId}/details", async (string modId, HttpRequest request,
    IConfiguration config, MongoContext db) =>
{
    var token = BearerToken(request);
    var user = await db.UserFromTokenAsync(token);
    if (user is null) return Results.Unauthorized();

    var admins = config.GetSection("Admin:DiscordIds").Get<string[]>() ?? Array.Empty<string>();

    var mod = await db.Mods.Find(m => m.ModId == modId).FirstOrDefaultAsync();
    if (mod is null) return Results.NotFound();
    if (mod.OwnerDiscordId != user.DiscordId && !admins.Contains(user.DiscordId))
        return Results.BadRequest("You do not own this mod.");

    if (!request.HasFormContentType) return Results.BadRequest("Expected multipart form data.");
    var form = await request.ReadFormAsync();

    var updates = new List<UpdateDefinition<ModDoc>>();

    var newName = form["name"].ToString().Trim();
    if (!string.IsNullOrEmpty(newName) && !string.Equals(newName, mod.Name, StringComparison.Ordinal))
    {
        var nameError = ModNaming.Validate(newName);
        if (nameError is not null) return Results.BadRequest(nameError);

        var folderName = ModNaming.ToFolderName(newName, mod.ModId);
        var allMods = await db.Mods.Find(_ => true).ToListAsync();
        var collision = allMods.FirstOrDefault(other =>
            other.ModId != mod.ModId &&
            string.Equals(ModNaming.ToFolderName(other.Name, other.ModId), folderName,
                StringComparison.OrdinalIgnoreCase));
        if (collision is not null)
            return Results.BadRequest($"The name '{newName}' is already taken by another mod.");

        updates.Add(Builders<ModDoc>.Update.Set(m => m.Name, newName));
    }

    var description = form["description"].ToString().Trim();
    if (!string.IsNullOrEmpty(description))
        updates.Add(Builders<ModDoc>.Update.Set(m => m.Description, description));

    if (form.ContainsKey("tags") && !string.IsNullOrWhiteSpace(form["tags"]))
    {
        List<string> newTags;
        try
        {
            newTags = JsonSerializer.Deserialize<List<string>>(form["tags"]!) ?? new List<string>();
        }
        catch { return Results.BadRequest("tags must be a JSON array of strings."); }

        if (newTags.Count > ModTags.MaxTagsPerMod)
            return Results.BadRequest($"A mod can have at most {ModTags.MaxTagsPerMod} tags.");
        foreach (var tag in newTags)
        {
            if (!ModTags.IsValid(tag))
                return Results.BadRequest($"Unknown tag '{tag}'. Allowed: {string.Join(", ", ModTags.All)}.");
        }
        updates.Add(Builders<ModDoc>.Update.Set(m => m.Tags,
            newTags.Select(ModTags.Canonical).Distinct().ToList()));
    }

    // Dependencies apply to the latest revision (the one new installs receive).
    if (form.ContainsKey("dependencies"))
    {
        Dictionary<string, string> newDeps;
        try
        {
            newDeps = string.IsNullOrWhiteSpace(form["dependencies"])
                ? new Dictionary<string, string>()
                : JsonSerializer.Deserialize<Dictionary<string, string>>(form["dependencies"]!)
                  ?? new Dictionary<string, string>();
        }
        catch { return Results.BadRequest("dependencies must be a JSON object of id → constraint."); }

        foreach (var depId in newDeps.Keys)
        {
            if (string.Equals(depId, modId, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest("A mod cannot depend on itself.");
            var depExists = await db.Mods.Find(m => m.ModId == depId).AnyAsync();
            if (!depExists) return Results.BadRequest($"Dependency '{depId}' does not exist in the database.");
        }

        var latest = mod.Versions.OrderByDescending(v => v.Revision).FirstOrDefault();
        if (latest is not null)
        {
            latest.Dependencies = newDeps;
            updates.Add(Builders<ModDoc>.Update.Set(m => m.Versions, mod.Versions));
        }
    }

    if (form.ContainsKey("conflicts"))
    {
        List<string> newConflicts;
        try
        {
            newConflicts = string.IsNullOrWhiteSpace(form["conflicts"])
                ? new List<string>()
                : JsonSerializer.Deserialize<List<string>>(form["conflicts"]!) ?? new List<string>();
        }
        catch { return Results.BadRequest("conflicts must be a JSON array of mod ids."); }

        newConflicts = newConflicts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var conflictId in newConflicts)
        {
            if (string.Equals(conflictId, modId, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest("A mod cannot conflict with itself.");
            var conflictExists = await db.Mods.Find(m => m.ModId == conflictId).AnyAsync();
            if (!conflictExists) return Results.BadRequest($"Conflict '{conflictId}' does not exist in the database.");
        }
        updates.Add(Builders<ModDoc>.Update.Set(m => m.Conflicts, newConflicts));
    }

    var iconFile = form.Files.GetFile("icon");
    if (iconFile is not null && iconFile.Length > 0)
    {
        if (iconFile.Length > 1024 * 1024) return Results.BadRequest("Icon exceeds 1 MB limit.");
        using var iconStream = new MemoryStream();
        await iconFile.OpenReadStream().CopyToAsync(iconStream);

        var normalized = IconProcessor.Normalize(iconStream.ToArray());
        if (normalized is null)
            return Results.BadRequest("Icon could not be read as an image.");

        var newIconId = await db.Files.UploadFromBytesAsync($"{modId}-icon.png", normalized);
        updates.Add(Builders<ModDoc>.Update.Set(m => m.IconFileId, newIconId));

        if (mod.IconFileId is not null)
        {
            try { await db.Files.DeleteAsync(mod.IconFileId.Value); } catch { }
        }
    }

    if (updates.Count > 0)
        await db.Mods.UpdateOneAsync(m => m.ModId == modId,
            Builders<ModDoc>.Update.Combine(updates));
    return Results.Json(new { id = modId, updated = updates.Count > 0 });
});

// ---------- Upload (new mod, or update with replacement) ----------
app.MapPost("/api/mods/upload", async (HttpRequest request, IConfiguration config, MongoContext db) =>
{
    var token = BearerToken(request);
    var user = await db.UserFromTokenAsync(token);
    if (user is null) return Results.Unauthorized();

    var adminIds = config.GetSection("Admin:DiscordIds").Get<string[]>() ?? Array.Empty<string>();
    var callerIsAdmin = adminIds.Contains(user.DiscordId);
    var autoApproved = callerIsAdmin || user.Verified;

    if (!AllowUpload(user.DiscordId))
        return Results.BadRequest("Upload limit reached (10 per hour). Try again later.");

    if (!autoApproved)
    {
        var myMods = await db.Mods.Find(m => m.OwnerDiscordId == user.DiscordId).ToListAsync();
        var pendingCount = myMods.Sum(m => m.Versions.Count(v => !v.Approved));
        if (pendingCount >= 5)
            return Results.BadRequest(
                "You have 5 uploads awaiting review. Please wait for approval before uploading more.");
    }

    if (!request.HasFormContentType) return Results.BadRequest("Expected multipart form data.");
    var form = await request.ReadFormAsync();

    var modFile = form.Files.GetFile("modFile");
    if (modFile is null || modFile.Length == 0) return Results.BadRequest("modFile is required.");
    if (modFile.Length > 50 * 1024 * 1024) return Results.BadRequest("Mod file exceeds 50 MB limit.");

    var description = form["description"].ToString().Trim();
    var providedName = form["name"].ToString().Trim();
    var changelog = form["changelog"].ToString().Trim();

    // Privacy: omitted field on an update means "keep existing".
    bool? isPrivateProvided = form.ContainsKey("isPrivate")
        ? string.Equals(form["isPrivate"].ToString(), "true", StringComparison.OrdinalIgnoreCase)
        : null;

    // If the form omits the dependencies field entirely, this is an update that
    // wants to KEEP the existing dependencies (resolved after we find the mod).
    var depsProvided = form.ContainsKey("dependencies");
    var dependencies = new Dictionary<string, string>();
    if (depsProvided && !string.IsNullOrWhiteSpace(form["dependencies"]))
    {
        try
        {
            dependencies = JsonSerializer.Deserialize<Dictionary<string, string>>(form["dependencies"]!)
                           ?? new Dictionary<string, string>();
        }
        catch { return Results.BadRequest("dependencies must be a JSON object of id → constraint."); }
    }

    // Tags: omitted field on an update means "keep existing".
    var tagsProvided = form.ContainsKey("tags");
    var tags = new List<string>();
    if (tagsProvided && !string.IsNullOrWhiteSpace(form["tags"]))
    {
        try
        {
            tags = JsonSerializer.Deserialize<List<string>>(form["tags"]!) ?? new List<string>();
        }
        catch { return Results.BadRequest("tags must be a JSON array of strings."); }
    }
    if (tags.Count > ModTags.MaxTagsPerMod)
        return Results.BadRequest($"A mod can have at most {ModTags.MaxTagsPerMod} tags.");
    foreach (var tag in tags)
    {
        if (!ModTags.IsValid(tag))
            return Results.BadRequest($"Unknown tag '{tag}'. Allowed: {string.Join(", ", ModTags.All)}.");
    }
    tags = tags.Select(ModTags.Canonical).Distinct().ToList();

    // Conflicts: omitted field on an update means "keep existing".
    var conflictsProvided = form.ContainsKey("conflicts");
    var conflicts = new List<string>();
    if (conflictsProvided && !string.IsNullOrWhiteSpace(form["conflicts"]))
    {
        try
        {
            conflicts = JsonSerializer.Deserialize<List<string>>(form["conflicts"]!) ?? new List<string>();
        }
        catch { return Results.BadRequest("conflicts must be a JSON array of mod ids."); }
    }
    conflicts = conflicts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    var isZip = modFile.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    PluginInfo? plugin = null;
    var payloadFiles = new List<(string RelativePath, byte[] Bytes)>();

    await using (var uploadStream = modFile.OpenReadStream())
    using (var buffer = new MemoryStream())
    {
        await uploadStream.CopyToAsync(buffer);
        buffer.Position = 0;

        if (isZip)
        {
            const long maxDecompressed = 256L * 1024 * 1024;
            const int maxEntries = 2000;
            long totalDecompressed = 0;

            using var zip = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count > maxEntries)
                return Results.BadRequest("Zip contains too many files.");

            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                if (entry.FullName.Contains("..")) return Results.BadRequest("Zip contains unsafe paths.");
                using var entryStream = new MemoryStream();
                await entry.Open().CopyToAsync(entryStream);
                var bytes = entryStream.ToArray();

                totalDecompressed += bytes.Length;
                if (totalDecompressed > maxDecompressed)
                    return Results.BadRequest("Zip decompresses beyond the allowed size.");

                payloadFiles.Add((entry.FullName, bytes));
                if (plugin is null && entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    plugin = DllInspector.Inspect(new MemoryStream(bytes));
            }
        }
        else
        {
            var bytes = buffer.ToArray();
            payloadFiles.Add((modFile.FileName, bytes));
            plugin = DllInspector.Inspect(new MemoryStream(bytes));
        }
    }

    if (plugin is null)
        return Results.BadRequest("No [BepInPlugin] attribute found. Is this a BepInEx plugin?");
    if (!Version.TryParse(plugin.Version, out _))
        return Results.BadRequest($"Plugin version '{plugin.Version}' is not a valid version number (e.g. 1.0.0).");

    var existing = await db.Mods.Find(m => m.ModId == plugin.Guid).FirstOrDefaultAsync();
    if (existing is not null && existing.OwnerDiscordId != user.DiscordId && !callerIsAdmin)
        return Results.BadRequest("This mod ID is owned by another user.");

    // Resolve the display name: creator-provided for new mods (required); on
    // updates an omitted/empty name keeps the existing one.
    string displayName;
    if (existing is null)
    {
        var nameError = ModNaming.Validate(providedName);
        if (nameError is not null) return Results.BadRequest(nameError);
        displayName = providedName;
    }
    else
    {
        displayName = string.IsNullOrEmpty(providedName) ? existing.Name : providedName;
        var nameError = ModNaming.Validate(displayName);
        if (nameError is not null) return Results.BadRequest(nameError);
    }

    // Names must be unique after folder sanitization: the loader installs mods
    // into folders named after the display name, so collisions would overlap
    // on players' machines.
    var folderName = ModNaming.ToFolderName(displayName, plugin.Guid);
    var allMods = await db.Mods.Find(_ => true).ToListAsync();
    var collision = allMods.FirstOrDefault(m =>
        m.ModId != plugin.Guid &&
        string.Equals(ModNaming.ToFolderName(m.Name, m.ModId), folderName,
            StringComparison.OrdinalIgnoreCase));
    if (collision is not null)
        return Results.BadRequest($"The name '{displayName}' is already taken by another mod.");

    // Version bumps are NOT required: the server assigns an incrementing revision,
    // and the loader detects updates by revision, not by version string.
    var revision = (existing?.Versions.FirstOrDefault()?.Revision ?? 0) + 1;

    if (!depsProvided && existing is not null)
        dependencies = new Dictionary<string, string>(
            existing.Versions.FirstOrDefault()?.Dependencies ?? new Dictionary<string, string>());

    if (!tagsProvided && existing is not null)
        tags = new List<string>(existing.Tags);

    if (!conflictsProvided && existing is not null)
        conflicts = new List<string>(existing.Conflicts);

    foreach (var depId in dependencies.Keys)
    {
        if (string.Equals(depId, plugin.Guid, StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest("A mod cannot depend on itself.");
        var depExists = await db.Mods.Find(m => m.ModId == depId).AnyAsync();
        if (!depExists) return Results.BadRequest($"Dependency '{depId}' does not exist in the database.");
    }

    foreach (var conflictId in conflicts)
    {
        if (string.Equals(conflictId, plugin.Guid, StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest("A mod cannot conflict with itself.");
        var conflictExists = await db.Mods.Find(m => m.ModId == conflictId).AnyAsync();
        if (!conflictExists) return Results.BadRequest($"Conflict '{conflictId}' does not exist in the database.");
    }

    byte[] packagedZip;
    using (var output = new MemoryStream())
    {
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = JsonSerializer.Serialize(new
            {
                id = plugin.Guid,
                name = displayName,
                version = plugin.Version,
                revision,
                author = user.Username,
                description,
                dependencies,
            }, new JsonSerializerOptions { WriteIndented = true });

            var manifestEntry = zip.CreateEntry("manifest.json");
            await using (var w = new StreamWriter(manifestEntry.Open())) await w.WriteAsync(manifest);

            foreach (var (path, bytes) in payloadFiles)
            {
                var entry = zip.CreateEntry(path);
                await using var es = entry.Open();
                await es.WriteAsync(bytes);
            }
        }
        packagedZip = output.ToArray();
    }

    var sha256 = Convert.ToHexString(SHA256.HashData(packagedZip)).ToLowerInvariant();
    var zipFileId = await db.Files.UploadFromBytesAsync($"{plugin.Guid}-r{revision}.zip", packagedZip);

    ObjectId? iconFileId = existing?.IconFileId;
    var iconFile = form.Files.GetFile("icon");
    if (iconFile is not null && iconFile.Length > 0)
    {
        if (iconFile.Length > 1024 * 1024) return Results.BadRequest("Icon exceeds 1 MB limit.");
        using var iconStream = new MemoryStream();
        await iconFile.OpenReadStream().CopyToAsync(iconStream);

        var normalized = IconProcessor.Normalize(iconStream.ToArray());
        if (normalized is null)
            return Results.BadRequest("Icon could not be read as an image.");

        iconFileId = await db.Files.UploadFromBytesAsync($"{plugin.Guid}-icon.png", normalized);
        if (existing?.IconFileId is not null)
        {
            try { await db.Files.DeleteAsync(existing.IconFileId.Value); } catch { }
        }
    }

    var versionDoc = new ModVersionDoc
    {
        Version = plugin.Version,
        Revision = revision,
        ZipFileId = zipFileId,
        Sha256 = sha256,
        Approved = autoApproved,
        Changelog = changelog,
        Dependencies = dependencies,
        UploadedUtc = DateTime.UtcNow,
    };

    if (existing is null)
    {
        await db.Mods.InsertOneAsync(new ModDoc
        {
            ModId = plugin.Guid,
            Name = displayName,
            Description = description,
            Author = user.Username,
            OwnerDiscordId = user.DiscordId,
            IconFileId = iconFileId,
            Tags = tags,
            Conflicts = conflicts,
            IsPrivate = isPrivateProvided ?? false,
            Versions = { versionDoc },
        });
    }
    else
    {
        // Version history: keep the newest 3 revisions; delete zips beyond that.
        var kept = new List<ModVersionDoc> { versionDoc };
        kept.AddRange(existing.Versions.OrderByDescending(v => v.Revision));
        var trimmed = kept.Skip(3).ToList();
        kept = kept.Take(3).ToList();
        foreach (var old in trimmed)
        {
            try { await db.Files.DeleteAsync(old.ZipFileId); } catch { }
        }

        await db.Mods.UpdateOneAsync(m => m.ModId == plugin.Guid,
            Builders<ModDoc>.Update
                .Set(m => m.Name, displayName)
                .Set(m => m.Description, string.IsNullOrEmpty(description) ? existing.Description : description)
                .Set(m => m.IconFileId, iconFileId)
                .Set(m => m.Tags, tags)
                .Set(m => m.Conflicts, conflicts)
                .Set(m => m.IsPrivate, isPrivateProvided ?? existing.IsPrivate)
                .Set(m => m.Versions, kept));
    }

    var isUpdate = existing is not null;
    return Results.Json(new
    {
        id = plugin.Guid,
        name = displayName,
        version = plugin.Version,
        revision,
        sha256,
        updated = isUpdate,
        pending = !autoApproved,
    });
});

app.Run();