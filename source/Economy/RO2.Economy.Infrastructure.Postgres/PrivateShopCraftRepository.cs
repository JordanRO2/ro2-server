using RO2.Economy.Domain;
using RO2.Persistence.Postgres;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace RO2.Economy.Infrastructure.Postgres;

/// <summary>
/// PostgreSQL implementation of <see cref="IPrivateShopCraftRepository"/>: one row per character in the
/// <c>private_shop_crafts</c> table, the character's private-shop craft offers held as an ordered <c>jsonb</c>
/// array. Mirrors the Mongo provider's behaviour exactly: <see cref="GetOffersAsync"/> returns the stored list in
/// insertion order or an empty list when the character never saved one, and <see cref="ReplaceOffersAsync"/> is a
/// whole-list upsert (create on first save, full replace afterwards, last writer wins).
/// </summary>
/// <remarks>
/// The list is only ever read and written whole and its order is the order the client draws (the Mongo document
/// kept it as an ordered array), so it maps to a single <c>jsonb</c> array column rather than a child table:
/// nothing queries, pages or compare-and-sets inside it, and a JSON array preserves order and duplicates.
/// The <c>uint</c> character id (Mongo <c>_id</c>) maps to <c>bigint</c> (Postgres has no unsigned integers). Each
/// entry stores the <c>ushort</c> craft id as a JSON number and the wire's <c>ulong</c> price as its signed
/// 64-bit bit pattern (<c>unchecked((long)price)</c>), exactly as the Mongo entry does, so a price above
/// <see cref="long.MaxValue"/> round-trips bit for bit and jsonb numeric keeps every digit. No cap or validation
/// is applied, as in Mongo. The Mongo upsert (<c>ReplaceOne … IsUpsert</c>) is a single
/// <c>INSERT … ON CONFLICT (character_id) DO UPDATE</c>, so two concurrent first saves cannot fail with a
/// duplicate key. <c>updated_at</c> is stamped with <see cref="DateTime.UtcNow"/> on every save.
/// </remarks>
public sealed class PrivateShopCraftRepository : IPrivateShopCraftRepository
{
    /// <summary>The TableName table name.</summary>
    public const string TableName = "private_shop_crafts";

    private readonly PostgresDbContext _db;

    /// <summary>Creates the repository over the given database context.</summary>
    public PrivateShopCraftRepository(PostgresDbContext db) => _db = db;

 /// <summary>Wire shape of one offer inside the <c>offers</c> jsonb array; <c>Price</c> is the u64 bit pattern.</summary>
    private sealed record StoredOffer(ushort CraftId, long Price);

    /// <inheritdoc />
    public async Task<IReadOnlyList<PrivateShopCraftOffer>> GetOffersAsync(uint characterId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT offers FROM {TableName} WHERE character_id = @id", conn);
        cmd.Parameters.AddWithValue("id", (long)characterId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return Array.Empty<PrivateShopCraftOffer>(); 

        var stored = JsonSerializer.Deserialize<List<StoredOffer>>(reader.GetString(0)) ?? new List<StoredOffer>();
        return stored.Select(e => new PrivateShopCraftOffer(e.CraftId, unchecked((ulong)e.Price))).ToList();
    }

    /// <inheritdoc />
    public async Task ReplaceOffersAsync(uint characterId, IReadOnlyList<PrivateShopCraftOffer> offers, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(
            offers.Select(o => new StoredOffer(o.CraftId, unchecked((long)o.Price))).ToList());

        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {TableName} (character_id, offers, updated_at) VALUES (@id, @offers, @now) " +
            "ON CONFLICT (character_id) DO UPDATE SET offers = EXCLUDED.offers, updated_at = EXCLUDED.updated_at",
            conn);
        cmd.Parameters.AddWithValue("id", (long)characterId);
        cmd.Parameters.AddWithValue("offers", NpgsqlDbType.Jsonb, json);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
