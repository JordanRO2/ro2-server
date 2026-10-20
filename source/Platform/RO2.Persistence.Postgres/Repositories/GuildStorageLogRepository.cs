using Npgsql;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;

namespace RO2.Persistence.Postgres.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="IGuildStorageLog"/> over two append-only tables:
/// <c>guild_storage_log</c> (the item history) and <c>guild_funds_log</c> (the funds history). Mirrors the
/// Mongo provider's behaviour exactly: every method is best effort and swallows failures (a failed write is
/// lost, a failed read is an empty page with a zero total), a guild is identified by its 16-byte GUID, and a
/// page is the lines of the last <c>days</c> days (all of them when 0), newest first.
/// </summary>
/// <remarks>
/// The guild GUID is stored as <c>bytea</c> (the Mongo document kept its hex string; the raw bytes are the
/// same identity and need no encoding). The <c>uint</c> item id maps to <c>bigint</c> (Postgres has no
/// unsigned integers) and the <c>byte</c> kind to <c>smallint</c>. There is no compare-and-set here: both
/// logs are insert-only. Newest-first ordering is <c>when_utc DESC</c> with the identity <c>seq DESC</c> as a
/// tie-break so lines written within the same clock tick page deterministically (Mongo left ties unordered).
/// The time window is judged in-process against <see cref="DateTime.UtcNow"/>, as the Mongo path does.
/// </remarks>
public sealed class GuildStorageLogRepository : IGuildStorageLog
{
    public const string ItemTableName = "guild_storage_log";
    public const string FundsTableName = "guild_funds_log";

    private readonly PostgresDbContext _db;

    public GuildStorageLogRepository(PostgresDbContext db) => _db = db;

    public async Task AddAsync(byte[] guid16, string actorName, byte kind, uint itemId, int count)
    {
        try
        {
            await using var conn = await _db.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand(
                $"INSERT INTO {ItemTableName} (guild_guid, when_utc, actor_name, kind, item_id, count) " +
                "VALUES (@g, @w, @a, @k, @i, @c)", conn);
            cmd.Parameters.AddWithValue("g", guid16);
            cmd.Parameters.AddWithValue("w", DateTime.UtcNow);
            cmd.Parameters.AddWithValue("a", actorName ?? string.Empty);
            cmd.Parameters.AddWithValue("k", (short)kind);
            cmd.Parameters.AddWithValue("i", (long)itemId);
            cmd.Parameters.AddWithValue("c", count);
            await cmd.ExecuteNonQueryAsync();
        }
        catch {   }
    }

    public async Task<(long Total, IReadOnlyList<GuildStorageLogEntry> Lines)> GetPageAsync(byte[] guid16, int page, int days, int pageSize)
    {
        try
        {
            await using var conn = await _db.OpenConnectionAsync();
            var since = days > 0 ? DateTime.UtcNow.AddDays(-days) : (DateTime?)null;
            var total = await CountAsync(conn, ItemTableName, guid16, since);
            var size = Math.Max(1, pageSize);
            var offset = Math.Max(0, page) * size;

            await using var cmd = new NpgsqlCommand(
                $"SELECT when_utc, actor_name, kind, item_id, count FROM {ItemTableName} " +
                "WHERE guild_guid = @g AND (@since::timestamptz IS NULL OR when_utc >= @since) " +
                "ORDER BY when_utc DESC, seq DESC OFFSET @o LIMIT @l", conn);
            cmd.Parameters.AddWithValue("g", guid16);
            cmd.Parameters.Add(new NpgsqlParameter("since", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)since ?? DBNull.Value });
            cmd.Parameters.AddWithValue("o", (long)offset);
            cmd.Parameters.AddWithValue("l", (long)size);
            var lines = new List<GuildStorageLogEntry>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                lines.Add(new GuildStorageLogEntry
                {
                    WhenUtc = reader.GetDateTime(0),
                    ActorName = reader.GetString(1),
                    Kind = (byte)reader.GetInt16(2),
                    ItemId = (uint)reader.GetInt64(3),
                    Count = reader.GetInt32(4),
                });
            }
            return (total, lines);
        }
        catch
        {
            return (0, Array.Empty<GuildStorageLogEntry>());
        }
    }

    public async Task AddFundsAsync(byte[] guid16, string actorName, byte kind, long amount)
    {
        try
        {
            await using var conn = await _db.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand(
                $"INSERT INTO {FundsTableName} (guild_guid, when_utc, actor_name, kind, amount) " +
                "VALUES (@g, @w, @a, @k, @m)", conn);
            cmd.Parameters.AddWithValue("g", guid16);
            cmd.Parameters.AddWithValue("w", DateTime.UtcNow);
            cmd.Parameters.AddWithValue("a", actorName ?? string.Empty);
            cmd.Parameters.AddWithValue("k", (short)kind);
            cmd.Parameters.AddWithValue("m", amount);
            await cmd.ExecuteNonQueryAsync();
        }
        catch {   }
    }

    public async Task<(long Total, IReadOnlyList<GuildFundsLogEntry> Lines)> GetFundsPageAsync(byte[] guid16, int page, int days, int pageSize)
    {
        try
        {
            await using var conn = await _db.OpenConnectionAsync();
            var since = days > 0 ? DateTime.UtcNow.AddDays(-days) : (DateTime?)null;
            var total = await CountAsync(conn, FundsTableName, guid16, since);
            var size = Math.Max(1, pageSize);
            var offset = Math.Max(0, page) * size;

            await using var cmd = new NpgsqlCommand(
                $"SELECT when_utc, actor_name, kind, amount FROM {FundsTableName} " +
                "WHERE guild_guid = @g AND (@since::timestamptz IS NULL OR when_utc >= @since) " +
                "ORDER BY when_utc DESC, seq DESC OFFSET @o LIMIT @l", conn);
            cmd.Parameters.AddWithValue("g", guid16);
            cmd.Parameters.Add(new NpgsqlParameter("since", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)since ?? DBNull.Value });
            cmd.Parameters.AddWithValue("o", (long)offset);
            cmd.Parameters.AddWithValue("l", (long)size);
            var lines = new List<GuildFundsLogEntry>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                lines.Add(new GuildFundsLogEntry
                {
                    WhenUtc = reader.GetDateTime(0),
                    ActorName = reader.GetString(1),
                    Kind = (byte)reader.GetInt16(2),
                    Amount = reader.GetInt64(3),
                });
            }
            return (total, lines);
        }
        catch
        {
            return (0, Array.Empty<GuildFundsLogEntry>());
        }
    }

 /// <summary>How many lines of <paramref name="table"/> the guild's window holds (the page's Total).</summary>
    private static async Task<long> CountAsync(NpgsqlConnection conn, string table, byte[] guid16, DateTime? since)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT count(*) FROM {table} " +
            "WHERE guild_guid = @g AND (@since::timestamptz IS NULL OR when_utc >= @since)", conn);
        cmd.Parameters.AddWithValue("g", guid16);
        cmd.Parameters.Add(new NpgsqlParameter("since", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)since ?? DBNull.Value });
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }
}
