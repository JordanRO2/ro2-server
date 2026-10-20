using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;

namespace RO2.Persistence.Postgres.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="ICardRegistryRepository"/> over the <c>card_registries</c> table:
/// one row per character (<c>character_id</c> = the character id, the Mongo <c>_id</c>), with the registered
/// card list as one <c>cards</c> jsonb column (an ordered array of <c>{CardId, State}</c> objects) and the
/// <c>updated_at</c> stamp. Mirrors the Mongo provider exactly: the document is only ever read whole and
/// replaced whole (no per-card query or CAS), so a nested jsonb array keeps the list order and duplicates the
/// Mongo array kept, and the upsert is a single <c>INSERT … ON CONFLICT DO UPDATE</c> (the Mongo
/// <c>ReplaceOne</c> with <c>IsUpsert</c>; last writer wins).
/// </summary>
/// <remarks>
/// Postgres has no unsigned integers, so the <c>uint</c> character id maps to <c>bigint</c> (bound with a
/// <c>long</c> cast, read back through <c>(uint)GetInt64</c>); the card ids and state bytes live inside the
/// jsonb as plain JSON numbers. <see cref="CardRegistryDocument.UpdatedAt"/> is stamped with
/// <see cref="DateTime.UtcNow"/> on save and bound as UTC to <c>timestamptz</c>.
/// </remarks>
public sealed class CardRegistryRepository : ICardRegistryRepository
{
    public const string TableName = "card_registries";

    private readonly PostgresDbContext _db;

    public CardRegistryRepository(PostgresDbContext db) => _db = db;

    public async Task<CardRegistryDocument> GetAsync(uint characterId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT cards, updated_at FROM {TableName} WHERE character_id = @id", conn);
        cmd.Parameters.AddWithValue("id", (long)characterId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        
        if (!await reader.ReadAsync(ct)) return new CardRegistryDocument { Id = characterId };

        var cards = JsonSerializer.Deserialize<List<CardRegistryEntry>>(reader.GetString(0)) ?? new List<CardRegistryEntry>();
        return new CardRegistryDocument
        {
            Id = characterId,
            Cards = cards,
            UpdatedAt = reader.GetDateTime(1),
        };
    }

    public async Task SaveAsync(CardRegistryDocument doc, CancellationToken ct = default)
    {
        
        doc.UpdatedAt = DateTime.UtcNow;

        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {TableName} (character_id, cards, updated_at) VALUES (@id, @cards, @at) " +
            "ON CONFLICT (character_id) DO UPDATE SET cards = EXCLUDED.cards, updated_at = EXCLUDED.updated_at",
            conn);
        cmd.Parameters.AddWithValue("id", (long)doc.Id);
        cmd.Parameters.Add(new NpgsqlParameter("cards", NpgsqlDbType.Jsonb)
        {
            Value = JsonSerializer.Serialize(doc.Cards),
        });
        cmd.Parameters.AddWithValue("at", NpgsqlDbType.TimestampTz, doc.UpdatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
