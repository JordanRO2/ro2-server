using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;

namespace RO2.Persistence.Postgres.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="IPetIncubatorRepository"/>: one row per character in the
/// <c>pet_incubators</c> table, the incubator slots held as an ordered <c>jsonb</c> array. Mirrors the Mongo
/// provider's behaviour exactly: every change is a plain read-modify-write of the whole slot list (the Zone host's
/// per-character request lock serialises it, there is no compare-and-set in either provider), a character with no
/// stored row reads as an empty list that is NOT written, a failed rule writes nothing, and a save is an upsert.
/// </summary>
/// <remarks>
/// The slot list (at most two entries) is never queried, paged or compare-and-set on its own, and the slot rules
/// stay in <see cref="PetIncubatorRules"/>, so it is a single <c>jsonb</c> column rather than a child table;
/// <c>jsonb</c> preserves array order, which matters because <see cref="PetIncubatorRules.RestoreSlot"/> re-appends
/// the restored slot at the end, as the Mongo list did. The <c>uint</c> character id (Mongo <c>_id</c>) maps to
/// <c>bigint</c> (Postgres has no unsigned integers); the <c>uint</c> incubator / egg ids and the <c>byte</c>
/// slot / charge counts are JSON numbers. <see cref="PetIncubatorSlot.StartUtc"/> and
/// <see cref="PetIncubatorSlot.EndUtc"/> are normalised to UTC and truncated to whole milliseconds on write, which
/// is what a BSON date round-trip did, and read back with Kind=Utc. Emptying the last slot leaves the row with an
/// empty array, exactly as the Mongo document kept an empty <c>Slots</c> list. The save is a single
/// <c>INSERT … ON CONFLICT DO UPDATE</c>, the SQL equivalent of the Mongo <c>ReplaceOne(IsUpsert)</c>.
/// </remarks>
public sealed class PetIncubatorRepository : IPetIncubatorRepository
{
    public const string TableName = "pet_incubators";

    private readonly PostgresDbContext _db;

    public PetIncubatorRepository(PostgresDbContext db) => _db = db;

 /// <summary>Wire shape of one slot inside the <c>slots</c> jsonb array.</summary>
    private sealed record StoredSlot(
        byte Slot,
        uint IncubatorId,
        byte Charges,
        uint EggId,
        DateTime StartUtc,
        DateTime EndUtc);

    public async Task<IReadOnlyList<PetIncubatorSlot>> GetSlotsAsync(uint characterId, CancellationToken ct = default)
        => await LoadAsync(characterId, ct);

    public async Task<bool> TryPlaceIncubatorAsync(uint characterId, byte slot, uint incubatorId, byte charges, DateTime nowUtc, CancellationToken ct = default)
    {
        var slots = await LoadAsync(characterId, ct);
        if (!PetIncubatorRules.TryPlace(slots, slot, incubatorId, charges, nowUtc)) return false;
        await SaveAsync(characterId, slots, ct);
        return true;
    }

    public async Task<bool> TryStartIncubationAsync(uint characterId, byte slot, uint incubatorId, uint eggId, DateTime startUtc, DateTime endUtc, CancellationToken ct = default)
    {
        var slots = await LoadAsync(characterId, ct);
        if (!PetIncubatorRules.TryStartIncubation(slots, slot, incubatorId, eggId, startUtc, endUtc)) return false;
        await SaveAsync(characterId, slots, ct);
        return true;
    }

    public async Task<PetIncubatorSlot?> TryCollectAsync(uint characterId, byte slot, uint incubatorId, DateTime nowUtc, CancellationToken ct = default)
    {
        var slots = await LoadAsync(characterId, ct);
        var s = PetIncubatorRules.CollectableSlot(slots, slot, incubatorId, nowUtc);
        if (s == null) return null;
        var before = PetIncubatorRules.Copy(s);
        PetIncubatorRules.ApplyCollect(slots, s, nowUtc);
        await SaveAsync(characterId, slots, ct);
        return before;
    }

    public async Task RestoreSlotAsync(uint characterId, PetIncubatorSlot before, CancellationToken ct = default)
    {
        var slots = await LoadAsync(characterId, ct);
        PetIncubatorRules.RestoreSlot(slots, before);
        await SaveAsync(characterId, slots, ct);
    }

    
    private async Task<List<PetIncubatorSlot>> LoadAsync(uint characterId, CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT slots FROM {TableName} WHERE character_id = @id", conn);
        cmd.Parameters.AddWithValue("id", (long)characterId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var slots = new List<PetIncubatorSlot>();
        if (!await reader.ReadAsync(ct)) return slots;

        var stored = JsonSerializer.Deserialize<List<StoredSlot>>(reader.GetString(0)) ?? new List<StoredSlot>();
        foreach (var e in stored)
        {
            slots.Add(new PetIncubatorSlot
            {
                Slot = e.Slot,
                IncubatorId = e.IncubatorId,
                Charges = e.Charges,
                EggId = e.EggId,
                StartUtc = e.StartUtc,
                EndUtc = e.EndUtc,
            });
        }
        return slots;
    }

    private async Task SaveAsync(uint characterId, List<PetIncubatorSlot> slots, CancellationToken ct)
    {
        var entries = new List<StoredSlot>();
        foreach (var s in slots)
        {
            entries.Add(new StoredSlot(
                s.Slot, s.IncubatorId, s.Charges, s.EggId, ToUtcMilliseconds(s.StartUtc), ToUtcMilliseconds(s.EndUtc)));
        }
        var json = JsonSerializer.Serialize(entries);

        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {TableName} (character_id, slots) VALUES (@id, @slots) " +
            "ON CONFLICT (character_id) DO UPDATE SET slots = EXCLUDED.slots",
            conn);
        cmd.Parameters.AddWithValue("id", (long)characterId);
        cmd.Parameters.AddWithValue("slots", NpgsqlDbType.Jsonb, json);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    
    private static DateTime ToUtcMilliseconds(DateTime value)
    {
        var utc = value.ToUniversalTime();
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    }
}
