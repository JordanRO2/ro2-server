using Npgsql;
using RO2.Application.Interfaces;

namespace RO2.Persistence.Postgres.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="IChannelColorRepository"/>: per-character community-channel tab
/// colours in the <c>chat_channel_colors</c> table. The Mongo provider kept one document per owner holding a
/// list of (index, argb) pairs; here each pair is one row keyed by (<c>owner_id</c>, <c>channel_index</c>),
/// because the interface only ever sets one channel and reads them all back — no nested document is needed.
/// </summary>
/// <remarks>
/// <see cref="SetColorAsync"/> is a single <c>INSERT … ON CONFLICT DO UPDATE</c>, which is the same
/// "replace that channel's value, add it if absent, leave other channels untouched" result the Mongo
/// read-modify-replace produced, but atomic. The <c>uint</c> owner id and ARGB map to <c>bigint</c>
/// (Postgres has no unsigned integers); the <c>ushort</c> channel index maps to <c>integer</c>. A channel the
/// player never recoloured has no row, so <see cref="GetColorsAsync"/> returns an empty map for an unknown owner.
/// </remarks>
public sealed class ChannelColorRepository : IChannelColorRepository
{
    public const string TableName = "chat_channel_colors";

    private readonly PostgresDbContext _db;

    public ChannelColorRepository(PostgresDbContext db) => _db = db;

    public async Task SetColorAsync(uint ownerId, ushort channelIndex, uint argb, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {TableName} (owner_id, channel_index, argb) VALUES (@o, @i, @c) " +
            "ON CONFLICT (owner_id, channel_index) DO UPDATE SET argb = EXCLUDED.argb",
            conn);
        cmd.Parameters.AddWithValue("o", (long)ownerId);
        cmd.Parameters.AddWithValue("i", (int)channelIndex);
        cmd.Parameters.AddWithValue("c", (long)argb);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyDictionary<ushort, uint>> GetColorsAsync(uint ownerId, CancellationToken ct = default)
    {
        var map = new Dictionary<ushort, uint>();
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT channel_index, argb FROM {TableName} WHERE owner_id = @o", conn);
        cmd.Parameters.AddWithValue("o", (long)ownerId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            map[(ushort)reader.GetInt32(0)] = (uint)reader.GetInt64(1);
        return map;
    }
}
