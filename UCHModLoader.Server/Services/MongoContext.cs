using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.GridFS;

namespace UCHModLoader.Server.Services;

public sealed class MongoContext
{
    public IMongoCollection<UserDoc> Users { get; }
    public IMongoCollection<ModDoc> Mods { get; }
    public IMongoCollection<PackDoc> Packs { get; }
    public GridFSBucket Files { get; }

    public MongoContext(IConfiguration config)
    {
        var client = new MongoClient(config["Mongo:ConnectionString"]);
        var db = client.GetDatabase(config["Mongo:Database"] ?? "uchmods");
        Users = db.GetCollection<UserDoc>("users");
        Mods = db.GetCollection<ModDoc>("mods");
        Packs = db.GetCollection<PackDoc>("packs");
        Files = new GridFSBucket(db);

        Users.Indexes.CreateOne(new CreateIndexModel<UserDoc>(
            Builders<UserDoc>.IndexKeys.Ascending(u => u.DiscordId),
            new CreateIndexOptions { Unique = true }));
        Mods.Indexes.CreateOne(new CreateIndexModel<ModDoc>(
            Builders<ModDoc>.IndexKeys.Ascending(m => m.ModId),
            new CreateIndexOptions { Unique = true }));
    }

    public async Task<UserDoc?> UserFromTokenAsync(string? bearerToken)
    {
        if (string.IsNullOrWhiteSpace(bearerToken)) return null;
        var user = await Users.Find(u => u.ApiToken == bearerToken).FirstOrDefaultAsync();
        return user is { Banned: false } ? user : null;
    }
}