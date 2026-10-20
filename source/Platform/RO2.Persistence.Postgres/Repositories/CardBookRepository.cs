using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;

namespace RO2.Persistence.Postgres.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="ICardBookRepository"/>: one row per character in the
/// <c>card_books</c> table, the registered cards held as an ordered <c>jsonb</c> array. Mirrors the Mongo
/// provider's behaviour exactly: the whole document is loaded and replaced as a unit, a character with no
/// stored book gets a fresh in-memory default that is NOT written, and a save is an upsert.
/// </summary>
/// <remarks>
/// The card list is never queried, paged or compare-and-set on its own (the put / take-off rules live in the
/// caller, which loads and replaces the whole document), so it is a single <c>jsonb</c> column rather than a
/// child table; <c>jsonb</c> preserves array order, which is the registration order the Mongo list kept. The
/// <c>uint</c> character id (Mongo <c>_id</c>) maps to <c>bigint</c> (Postgres has no unsigned integers). The
/// <c>ulong</c> origin item uid is stored as a JSON number (arbitrary precision in <c>jsonb</c>, so the full
/// ulong range round-trips). <see cref="CardBookEntry.RegisteredAt"/> is normalised to UTC and truncated to
/// whole milliseconds on write, which is what a BSON date round-trip did, and reads back with Kind=Utc.
/// The save is a single <c>INSERT … ON CONFLICT DO UPDATE</c>, the SQL equivalent of the Mongo
/// <c>ReplaceOne(IsUpsert)</c>.
/// </remarks>
public sealed class CardBookRepository : ICardBookRepository
{
    public const string TableName = "card_books";

    private readonly PostgresDbContext _db;

    public CardBookRepository(PostgresDbContext db) => _db = db;

 /// <summary>Wire shape of one entry inside the <c>cards</c> jsonb array.</summary>
    private sealed record StoredEntry(
        uint CardId,
        byte Grade,
        byte EnforceGrade,
        byte Kind,
        ulong OriginItemUid,
        DateTime RegisteredAt);

    public async Task<CardBookDocument> GetOrCreateAsync(uint characterId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT cards FROM {TableName} WHERE character_id = @id", conn);
        cmd.Parameters.AddWithValue("id", (long)characterId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new CardBookDocument { Id = characterId };      

        var stored = JsonSerializer.Deserialize<List<StoredEntry>>(reader.GetString(0)) ?? new List<StoredEntry>();
        var doc = new CardBookDocument { Id = characterId };
        foreach (var e in stored)
        {
            doc.Cards.Add(new CardBookEntry
            {
                CardId = e.CardId,
                Grade = e.Grade,
                EnforceGrade = e.EnforceGrade,
                Kind = e.Kind,
                OriginItemUid = e.OriginItemUid,
                RegisteredAt = e.RegisteredAt,
            });
        }
        return doc;
    }

    public async Task SaveAsync(CardBookDocument doc, CancellationToken ct = default)
    {
        var entries = new List<StoredEntry>();
        foreach (var c in doc.Cards ?? new List<CardBookEntry>())
        {
            entries.Add(new StoredEntry(
                c.CardId, c.Grade, c.EnforceGrade, c.Kind, c.OriginItemUid, ToUtcMilliseconds(c.RegisteredAt)));
        }
        var json = JsonSerializer.Serialize(entries);

        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {TableName} (character_id, cards) VALUES (@id, @cards) " +
            "ON CONFLICT (character_id) DO UPDATE SET cards = EXCLUDED.cards",
            conn);
        cmd.Parameters.AddWithValue("id", (long)doc.Id);
        cmd.Parameters.AddWithValue("cards", NpgsqlDbType.Jsonb, json);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    
    private static DateTime ToUtcMilliseconds(DateTime value)
    {
        var utc = value.ToUniversalTime();
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    }
}
