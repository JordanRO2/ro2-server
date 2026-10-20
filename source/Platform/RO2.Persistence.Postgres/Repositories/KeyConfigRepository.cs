using Npgsql;
using NpgsqlTypes;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;

namespace RO2.Persistence.Postgres.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="IKeyConfigRepository"/>: one flat row per character in the
/// <c>character_keyconfig</c> table (the Mongo collection of the same name, keyed by character id). Mirrors the
/// Mongo provider's behaviour exactly: <see cref="GetAsync"/> returns null when the character never saved,
/// <see cref="SaveAsync"/> replaces the whole record and creates it on first save, and
/// <see cref="ApplyUpdateAsync"/> keeps the stored <c>entries</c> for a flag-only request (mode 0, no records)
/// and replaces the record for every other request.
/// </summary>
/// <remarks>
/// The document is four scalars with no nested list, so it maps to plain columns rather than <c>jsonb</c>: the
/// <c>uint</c> character id is <c>bigint</c> (Postgres has no unsigned integers), the <c>ushort</c> mode is
/// <c>integer</c> (<c>smallint</c> is signed and cannot hold 0..65535), the verbatim binding records are
/// <c>bytea</c>, and the update stamp is <c>timestamptz</c>, normalised to UTC and truncated to whole
/// milliseconds on write (what a BSON date round-trip did) and read back with Kind=Utc. Save and both branches
/// of <see cref="ApplyUpdateAsync"/> are each one <c>INSERT … ON CONFLICT DO UPDATE</c> statement, so they are
/// atomic and a concurrent save cannot interleave: the full replace is the equivalent of
/// <c>ReplaceOne(IsUpsert)</c>, and the flag-only branch is the equivalent of the Mongo upsert with
/// <c>$set</c> on mode/updated and <c>$setOnInsert</c> on the empty entries (the conflict arm never touches
/// <c>entries</c>).
/// </remarks>
public sealed class KeyConfigRepository : IKeyConfigRepository
{
    public const string TableName = "character_keyconfig";

    private readonly PostgresDbContext _db;

    public KeyConfigRepository(PostgresDbContext db) => _db = db;

    public async Task<KeyConfigDocument?> GetAsync(uint characterId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT mode, entries, updated_utc FROM {TableName} WHERE character_id = @id", conn);
        cmd.Parameters.AddWithValue("id", (long)characterId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;              

        return new KeyConfigDocument
        {
            Id = characterId,
            Mode = (ushort)reader.GetInt32(0),
            Entries = reader.GetFieldValue<byte[]>(1),
            UpdatedUtc = reader.GetDateTime(2),
        };
    }

    public async Task SaveAsync(KeyConfigDocument doc, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {TableName} (character_id, mode, entries, updated_utc) VALUES (@id, @mode, @entries, @updated) " +
            "ON CONFLICT (character_id) DO UPDATE SET " +
            "mode = EXCLUDED.mode, entries = EXCLUDED.entries, updated_utc = EXCLUDED.updated_utc",
            conn);
        AddParameters(cmd, doc.Id, doc.Mode, doc.Entries, doc.UpdatedUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }

 /// <summary>True for the 1423 that only clears the flag: mode 0 with no records. Retail keeps the
 /// stored records for it.</summary>
    public static bool IsFlagOnlyUpdate(ushort mode, int entryBytes) => mode == 0 && entryBytes == 0;

    public async Task ApplyUpdateAsync(uint characterId, ushort mode, byte[] entries, DateTime updatedUtc, CancellationToken ct = default)
    {
        if (!IsFlagOnlyUpdate(mode, entries.Length))
        {
            await SaveAsync(new KeyConfigDocument
            {
                Id = characterId,
                Mode = mode,
                Entries = entries,
                UpdatedUtc = updatedUtc,
            }, ct);
            return;
        }

        await using var conn = await _db.OpenConnectionAsync(ct);
        
        
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {TableName} (character_id, mode, entries, updated_utc) VALUES (@id, @mode, ''::bytea, @updated) " +
            "ON CONFLICT (character_id) DO UPDATE SET mode = EXCLUDED.mode, updated_utc = EXCLUDED.updated_utc",
            conn);
        cmd.Parameters.AddWithValue("id", (long)characterId);
        cmd.Parameters.Add(new NpgsqlParameter("mode", NpgsqlDbType.Integer) { Value = (int)mode });
        cmd.Parameters.Add(new NpgsqlParameter("updated", NpgsqlDbType.TimestampTz) { Value = ToUtcMilliseconds(updatedUtc) });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void AddParameters(NpgsqlCommand cmd, uint characterId, ushort mode, byte[] entries, DateTime updatedUtc)
    {
        cmd.Parameters.AddWithValue("id", (long)characterId);
        cmd.Parameters.Add(new NpgsqlParameter("mode", NpgsqlDbType.Integer) { Value = (int)mode });
        cmd.Parameters.Add(new NpgsqlParameter("entries", NpgsqlDbType.Bytea) { Value = entries });
        cmd.Parameters.Add(new NpgsqlParameter("updated", NpgsqlDbType.TimestampTz) { Value = ToUtcMilliseconds(updatedUtc) });
    }

    
    private static DateTime ToUtcMilliseconds(DateTime value)
    {
        var utc = value.ToUniversalTime();
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    }
}
