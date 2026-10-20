using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using RO2.Persistence.Postgres;

namespace RO2.Server.Zone.Services;

/// <summary>
/// PostgreSQL implementation of <see cref="IGuildStore"/>. Each <see cref="Guild"/> aggregate is stored as one row
/// in the <c>guilds</c> table (<c>id</c> bigint and <c>data</c> jsonb). Loading reads every row, saving is a
/// whole-aggregate upsert, and deleting removes the row together with the guild's vault and roster. The in-memory
/// lock is excluded from the stored document.
/// </summary>
public sealed class PostgresGuildRepository : IGuildStore
{
    public const string TableName = "guilds";

    private readonly PostgresDbContext _db;

    public PostgresGuildRepository(PostgresDbContext db) => _db = db;

    public async Task<IReadOnlyList<Guild>> LoadAllAsync(CancellationToken ct = default)
    {
        var guilds = new List<Guild>();
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT data FROM {TableName}", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var guild = JsonSerializer.Deserialize<Guild>(reader.GetString(0));
            if (guild is not null) guilds.Add(guild);
        }
        return guilds;
    }

    public async Task SaveAsync(Guild guild, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(guild);
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {TableName} (id, data) VALUES (@id, @d) " +
            "ON CONFLICT (id) DO UPDATE SET data = EXCLUDED.data", conn);
        cmd.Parameters.AddWithValue("id", (long)guild.Id);
        cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Jsonb) { Value = json });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(uint guildId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"DELETE FROM {TableName} WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", (long)guildId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
