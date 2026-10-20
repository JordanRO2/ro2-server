using Npgsql;
using RO2.Application.Interfaces;

namespace RO2.Persistence.Postgres.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="ITimedEnchantRepository"/> over the shared <c>inventory_items</c>
/// table (owned by the Inventory aggregate; this repository adds no DDL). Each decrement is a field-level
/// compare-and-set on the item's id, enchant id and the seconds it read, writing only <c>enchant_seconds</c>,
/// so a restart or a handler rewrite between the read and the write is not undone.
/// </summary>
/// <remarks>
/// The unsigned ids and ushort enchant fields map to <c>bigint</c>. The Mongo provider reports success from
/// <c>ModifiedCount == 1</c>, so a matched-but-unchanged row counts as false; to reproduce that the update also
/// requires <c>enchant_seconds &lt;&gt; @new</c> (callers always decrement, so this never bites in practice).
/// </remarks>
public sealed class TimedEnchantRepository : ITimedEnchantRepository
{
    private readonly PostgresDbContext _db;

    public TimedEnchantRepository(PostgresDbContext db) => _db = db;

    public async Task<IReadOnlyList<RunningEnchant>> GetRunningWornAsync(uint characterId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT id, enchant_reinforce_id, enchant_seconds FROM inventory_items " +
            "WHERE character_id = @c AND storage_type = 1 AND enchant_reinforce_id <> 0 AND enchant_seconds > 0 " +
            "ORDER BY id", conn);
        cmd.Parameters.AddWithValue("c", (long)characterId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var running = new List<RunningEnchant>();
        while (await reader.ReadAsync(ct))
            running.Add(new RunningEnchant((ulong)reader.GetInt64(0), (ushort)reader.GetInt64(1), (ushort)reader.GetInt64(2)));
        return running;
    }

    public async Task<bool> TryAdvanceTimerAsync(ulong itemId, ushort expectedEnchantId, ushort expectedSeconds, ushort newSeconds, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "UPDATE inventory_items SET enchant_seconds = @new " +
            "WHERE id = @id AND enchant_reinforce_id = @expId AND enchant_seconds = @expSec AND enchant_seconds <> @new " +
            "RETURNING id", conn);
        cmd.Parameters.AddWithValue("id", (long)itemId);
        cmd.Parameters.AddWithValue("expId", (long)expectedEnchantId);
        cmd.Parameters.AddWithValue("expSec", (long)expectedSeconds);
        cmd.Parameters.AddWithValue("new", (long)newSeconds);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }
}
