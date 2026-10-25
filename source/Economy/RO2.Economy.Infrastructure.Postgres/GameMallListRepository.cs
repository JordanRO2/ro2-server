using RO2.Economy.Domain;
using RO2.Persistence.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace RO2.Economy.Infrastructure.Postgres;

/// <summary>
/// PostgreSQL implementation of <see cref="IGameMallListRepository"/>: the per-account game-mall item list 2
/// in the <c>game_mall_list_items</c> table. Mirrors the Mongo provider's behaviour — the list is newest
/// first, an id already in the list is skipped, and ids in one call are added one at a time to the front.
/// </summary>
/// <remarks>
/// The Mongo document (<c>_id</c> = account id, <c>Items</c> = ordered id list) becomes one row per
/// (account, id). A monotonically increasing identity column <c>seq</c> records insertion order, so "newest
/// first" is <c>ORDER BY seq DESC</c> and "put in front" is simply inserting the id last. The composite primary
/// key (account_id, item_id) enforces "no duplicates" in the database: <c>INSERT … ON CONFLICT DO NOTHING</c>
/// is the skip-a-known-id rule, and unlike the Mongo read-modify-replace it cannot lose a concurrent add or
/// store a repeat. The <c>uint</c> account and item ids map to <c>bigint</c> (Postgres has no unsigned
/// integers).
/// </remarks>
public sealed class GameMallListRepository : IGameMallListRepository
{
    /// <summary>The TableName table name.</summary>
    public const string TableName = "game_mall_list_items";

    private readonly PostgresDbContext _db;

    /// <summary>Creates the repository over the given database context.</summary>
    public GameMallListRepository(PostgresDbContext db) => _db = db;

    /// <inheritdoc />
    public async Task<IReadOnlyList<uint>> GetAsync(uint accountId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await ReadListAsync(conn, null, accountId, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<uint>> AddAsync(uint accountId, IReadOnlyList<uint> ids, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var insert = new NpgsqlCommand(
            $"INSERT INTO {TableName} (account_id, item_id) VALUES (@a, @i) ON CONFLICT (account_id, item_id) DO NOTHING",
            conn, tx))
        {
            insert.Parameters.AddWithValue("a", (long)accountId);
            var item = insert.Parameters.Add("i", NpgsqlDbType.Bigint);
            foreach (var id in ids)
            {
                
                item.Value = (long)id;
                await insert.ExecuteNonQueryAsync(ct);
            }
        }

        var list = await ReadListAsync(conn, tx, accountId, ct);
        await tx.CommitAsync(ct);
        return list;
    }

    private static async Task<IReadOnlyList<uint>> ReadListAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, uint accountId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT item_id FROM {TableName} WHERE account_id = @a ORDER BY seq DESC", conn, tx);
        cmd.Parameters.AddWithValue("a", (long)accountId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<uint>();
        while (await reader.ReadAsync(ct))
            result.Add((uint)reader.GetInt64(0));
        return result;
    }
}
