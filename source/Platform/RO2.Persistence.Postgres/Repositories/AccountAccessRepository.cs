using Npgsql;
using RO2.Application.Interfaces;

namespace RO2.Persistence.Postgres.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="IAccountAccessRepository"/>: a read-only lookup of an account's
/// GM/staff <c>AccessLevel</c> from the minimal <c>accounts</c> read model (<c>id</c>, <c>access_level</c>).
/// Mirrors the Mongo provider exactly — the account is found by id and its access byte returned, or 0 when
/// the row is absent (the Mongo <c>FirstOrDefault ?? 0</c>).
/// </summary>
/// <remarks>
/// Nothing here writes <c>access_level</c>; the account system owns that elsewhere. Selecting only
/// <c>id</c> and <c>access_level</c> keeps the same extra-column tolerance the Mongo read model got from
/// <c>BsonIgnoreExtraElements</c>. The <c>uint</c> account id maps to <c>bigint</c> (Postgres has no
/// unsigned integers) and the <c>byte</c> level to <c>smallint</c>.
/// </remarks>
public sealed class AccountAccessRepository : IAccountAccessRepository
{
    public const string TableName = "accounts";

    private readonly PostgresDbContext _db;

    public AccountAccessRepository(PostgresDbContext db) => _db = db;

    public async Task<byte> GetAccessLevelAsync(uint accountId)
    {
        await using var conn = await _db.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            $"SELECT access_level FROM {TableName} WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", (long)accountId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return 0;   
        return (byte)reader.GetInt16(0);
    }
}
