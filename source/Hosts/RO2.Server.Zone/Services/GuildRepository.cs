using MongoDB.Driver;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Server.Zone.Services;

/// <summary>
/// MongoDB persistence for guilds, implementing <see cref="IGuildStore"/>. Each guild is stored as a
/// whole <see cref="Guild"/> document keyed by its numeric id.
/// </summary>
/// <remarks>
/// Uses the <c>guilds</c> collection of the <c>ro2_server</c> database. Unknown fields in stored documents
/// are ignored. The type stays in RO2.Server.Zone because <see cref="Guild"/> is a Zone type.
/// </remarks>
public sealed class GuildRepository : IGuildStore
{
    private readonly IMongoCollection<Guild> _guilds;

    public GuildRepository(MongoDbContext context)
    {
        _guilds = context.GetCollection<Guild>("guilds");
    }

    public async Task<IReadOnlyList<Guild>> LoadAllAsync(CancellationToken ct = default)
        => await _guilds.Find(FilterDefinition<Guild>.Empty).ToListAsync(ct);

    public async Task<Guild?> GetByIdAsync(uint id)
        => await _guilds.Find(g => g.Id == id).FirstOrDefaultAsync();

 /// <summary>Creates or replaces the guild document. The write is atomic at the document level.</summary>
    public async Task SaveAsync(Guild guild, CancellationToken ct = default)
        => await _guilds.ReplaceOneAsync(g => g.Id == guild.Id, guild, new ReplaceOptions { IsUpsert = true }, ct);

    public async Task DeleteAsync(uint guildId, CancellationToken ct = default)
        => await _guilds.DeleteOneAsync(g => g.Id == guildId, ct);
}
