using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace UCHModLoader.Server.Services;

public sealed class UserDoc
{
    [BsonId] public ObjectId Id { get; set; }
    public string DiscordId { get; set; } = "";
    public string Username { get; set; } = "";
    public string AvatarUrl { get; set; } = "";
    public string ApiToken { get; set; } = "";
    public bool Banned { get; set; }
    public bool Verified { get; set; }   // uploads go live without review
    public DateTime CreatedUtc { get; set; }
}

public sealed class ModDoc
{
    [BsonId] public ObjectId Id { get; set; }
    public string ModId { get; set; } = "";          // BepInPlugin GUID, claimed by first uploader
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string OwnerDiscordId { get; set; } = "";
    public string Author { get; set; } = "";
    public ObjectId? IconFileId { get; set; }
    public bool Hidden { get; set; }                 // moderation kill switch
    public bool IsPrivate { get; set; }
    public List<string> PrivateKeys { get; set; } = new();          // unredeemed one-time keys
    public List<MultiUseKey> MultiUseKeys { get; set; } = new();    // shared keys, valid until expiry
    public List<string> AuthorizedDiscordIds { get; set; } = new(); // users granted access
    public long Downloads { get; set; }
    public List<string> UpvoterDiscordIds { get; set; } = new();
    public List<string> Tags { get; set; } = new();
    public List<string> Conflicts { get; set; } = new();   // mod ids known to conflict
    public List<ReportEntry> Reports { get; set; } = new();
    public List<ModVersionDoc> Versions { get; set; } = new();
}

public sealed class PackDoc
{
    [BsonId] public ObjectId Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> ModIds { get; set; } = new();
    public ObjectId? IconFileId { get; set; }
    public bool Hidden { get; set; }
}

public sealed class ModVersionDoc
{
    public string Version { get; set; } = "";
    public int Revision { get; set; }                // server-assigned, increments on every upload
    public ObjectId ZipFileId { get; set; }
    public string Sha256 { get; set; } = "";
    public bool Approved { get; set; }   // per-revision review gate
    public string Changelog { get; set; } = "";
    public Dictionary<string, string> Dependencies { get; set; } = new();
    public DateTime UploadedUtc { get; set; }
}

// One playtester's telemetry, keyed by the mod's ANONYMOUS player id (no Discord link).
// The mod posts its whole telemetry.json; Data holds it verbatim, with a few fields lifted
// out for easy querying. Upserted on PlayerId — last write wins.
public sealed class TelemetryDoc
{
    [BsonId] public ObjectId Id { get; set; }
    public string PlayerId { get; set; } = "";
    public string ModVersion { get; set; } = "";
    public bool AbReminders { get; set; }     // A/B bucket: reminders on/off
    public int SessionCount { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public BsonDocument Data { get; set; } = new();   // the raw telemetry.json
}

public sealed class MultiUseKey
{
    public string Key { get; set; } = "";
    public DateTime ExpiresUtc { get; set; }
}

public sealed class ReportEntry
{
    public string ReporterDiscordId { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTime ReportedUtc { get; set; }
}