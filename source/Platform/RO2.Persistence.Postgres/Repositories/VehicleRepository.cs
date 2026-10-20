using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;

namespace RO2.Persistence.Postgres.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="IVehicleRepository"/>: one row per character in the
/// <c>character_vehicles</c> table, the owned vehicles held as an ordered <c>jsonb</c> array. Mirrors the Mongo
/// provider's behaviour exactly: writes are optimistic, a read-modify-write replaces the row only if its
/// <c>updated_at</c> is still the value that was read, and retries from a fresh read otherwise, so two concurrent
/// grants both land.
/// </summary>
/// <remarks>
/// The vehicle list is never queried, paged or compare-and-set element by element (the grant / extend / cut rules
/// live in <see cref="VehicleRules"/> and run over the whole list), so it is a single <c>jsonb</c> column rather
/// than a child table; <c>jsonb</c> preserves array order, which is the stored order the Mongo list kept. The
/// <c>uint</c> character id (Mongo <c>_id</c>) maps to <c>bigint</c> (Postgres has no unsigned integers); the
/// <c>uint</c> vehicle id is a JSON number and the <c>double</c> OLE expiry is written with the shortest
/// round-trip text, which <c>jsonb numeric</c> keeps digit for digit, so it reads back bit for bit.
/// The compare-and-set is a single statement: <c>INSERT … ON CONFLICT DO NOTHING</c> when no row was read (a
/// concurrent creator makes it affect no row, the Mongo duplicate-key case) or
/// <c>UPDATE … WHERE character_id = @id AND updated_at = @read RETURNING</c> otherwise (no returned row is the
/// Mongo <c>MatchedCount == 0</c>). The stamp is kept at whole milliseconds, as Mongo kept it, and is forced to
/// differ from the one read so two writers can never tie.
/// </remarks>
public sealed class VehicleRepository : IVehicleRepository
{
    public const string TableName = "character_vehicles";

 /// <summary>Read-modify-write attempts before giving up on a row that keeps changing under us.</summary>
    private const int MaxAttempts = 5;

    private readonly PostgresDbContext _db;

    public VehicleRepository(PostgresDbContext db) => _db = db;

 /// <summary>Wire shape of one vehicle inside the <c>vehicles</c> jsonb array.</summary>
    private sealed record StoredVehicle(uint RidingId, double ExpiryOle);

 /// <summary>A stored row: the vehicles in stored order and the stamp the optimistic write compares.</summary>
    private sealed record StoredRow(List<OwnedVehicle> Vehicles, DateTime UpdatedUtc);

    public async Task<IReadOnlyList<OwnedVehicle>> GetAsync(uint characterId, CancellationToken ct = default)
    {
        var row = await ReadAsync(characterId, ct);
        return row?.Vehicles ?? new List<OwnedVehicle>();
    }

    public async Task<IReadOnlyList<OwnedVehicle>> GrantAsync(uint characterId, uint ridingId, uint? days, double nowOle, CancellationToken ct = default)
        => (await ModifyAsync(characterId, owned => VehicleRules.Grant(owned, ridingId, days, nowOle), ct))!;

    public Task<IReadOnlyList<OwnedVehicle>?> TryExtendTimedAsync(uint characterId, uint ridingId, uint days, double nowOle, CancellationToken ct = default)
        => ModifyAsync(characterId,
            owned => owned.FirstOrDefault(v => v.RidingId == ridingId) is { ExpiryOle: not 0 }
                ? VehicleRules.Grant(owned, ridingId, days, nowOle)
                : null,
            ct);

    public async Task<IReadOnlyList<OwnedVehicle>> LoadAndCutToWholeSecondsAsync(uint characterId, CancellationToken ct = default)
    {
        var row = await ReadAsync(characterId, ct);
        IReadOnlyList<OwnedVehicle> stored = row?.Vehicles ?? new List<OwnedVehicle>();
        var cut = VehicleRules.CutToWholeSeconds(stored);
        if (ReferenceEquals(cut, stored))
            return cut;
        try
        {
            
            await TryWriteAsync(characterId, row, cut, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
        return cut;
    }

    private async Task<StoredRow?> ReadAsync(uint characterId, CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT vehicles, updated_at FROM {TableName} WHERE character_id = @id", conn);
        cmd.Parameters.AddWithValue("id", (long)characterId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var stored = JsonSerializer.Deserialize<List<StoredVehicle>>(reader.GetString(0)) ?? new List<StoredVehicle>();
        var vehicles = new List<OwnedVehicle>(stored.Count);
        foreach (var v in stored)
            vehicles.Add(new OwnedVehicle { RidingId = v.RidingId, ExpiryOle = v.ExpiryOle });
        return new StoredRow(vehicles, DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc));
    }

 /// <summary>
 /// Reads the character's list, applies <paramref name="change"/> and stores the result, retrying from a fresh read
 /// when another writer got in between. Returns what <paramref name="change"/> returned; null (nothing stored)
 /// when it returned null.
 /// </summary>
    private async Task<IReadOnlyList<OwnedVehicle>?> ModifyAsync(uint characterId,
        Func<IReadOnlyList<OwnedVehicle>, IReadOnlyList<OwnedVehicle>?> change, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var row = await ReadAsync(characterId, ct);
            var next = change(row?.Vehicles ?? new List<OwnedVehicle>());
            if (next is null)
                return null;
            if (await TryWriteAsync(characterId, row, next, ct))
                return next;
        }
        throw new InvalidOperationException(
            $"The vehicle list of character {characterId} kept changing; gave up after {MaxAttempts} attempts.");
    }

 /// <summary>
 /// Stores <paramref name="next"/> if the row is still the one <paramref name="read"/> came from (insert when
 /// there was none). False when another writer changed it first.
 /// </summary>
    private async Task<bool> TryWriteAsync(uint characterId, StoredRow? read, IReadOnlyList<OwnedVehicle> next, CancellationToken ct)
    {
        
        
        var now = DateTime.UtcNow;
        var stamp = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        if (read is not null && stamp <= read.UpdatedUtc)
            stamp = read.UpdatedUtc.AddMilliseconds(1);

        var json = JsonSerializer.Serialize(next.Select(v => new StoredVehicle(v.RidingId, v.ExpiryOle)).ToList());

        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = read is null
            ? new NpgsqlCommand(
                $"INSERT INTO {TableName} (character_id, vehicles, updated_at) VALUES (@id, @vehicles, @stamp) " +
                "ON CONFLICT (character_id) DO NOTHING RETURNING character_id", conn)
            : new NpgsqlCommand(
                $"UPDATE {TableName} SET vehicles = @vehicles, updated_at = @stamp " +
                "WHERE character_id = @id AND updated_at = @read RETURNING character_id", conn);
        cmd.Parameters.AddWithValue("id", (long)characterId);
        cmd.Parameters.AddWithValue("vehicles", NpgsqlDbType.Jsonb, json);
        cmd.Parameters.AddWithValue("stamp", stamp);
        if (read is not null)
            cmd.Parameters.AddWithValue("read", read.UpdatedUtc);

        
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct);
    }
}
