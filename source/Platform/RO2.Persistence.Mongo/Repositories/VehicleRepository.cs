using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Persistence.Mongo.Repositories;

/// <summary>
/// MongoDB store for <see cref="IVehicleRepository"/>, one document per character in the <c>character_vehicles</c>
/// collection. Writes are optimistic: a read-modify-write replaces the document only if its <c>UpdatedUtc</c> still
/// matches the value read, and otherwise retries from a fresh read, so concurrent grants all apply.
/// </summary>
public sealed class VehicleRepository : IVehicleRepository
{
    private const string CollectionName = "character_vehicles";

 /// <summary>Read-modify-write attempts before giving up on a document that keeps changing under us.</summary>
    private const int MaxAttempts = 5;

 /// <summary>A character's owned vehicles. <c>Id</c> maps to Mongo <c>_id</c> and is the character id.</summary>
    private sealed class VehicleDocument
    {
        public uint Id { get; set; }

        public List<OwnedVehicle> Vehicles { get; set; } = new();

        public DateTime UpdatedUtc { get; set; }
    }

    private readonly IMongoCollection<VehicleDocument> _docs;

    public VehicleRepository(MongoDbContext context)
    {
        _docs = context.GetCollection<VehicleDocument>(CollectionName);
    }

    public async Task<IReadOnlyList<OwnedVehicle>> GetAsync(uint characterId, CancellationToken ct = default)
    {
        var doc = await ReadAsync(characterId, ct);
        return doc?.Vehicles ?? new List<OwnedVehicle>();
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
        var doc = await ReadAsync(characterId, ct);
        IReadOnlyList<OwnedVehicle> stored = doc?.Vehicles ?? new List<OwnedVehicle>();
        var cut = VehicleRules.CutToWholeSeconds(stored);
        if (ReferenceEquals(cut, stored))
            return cut;
        try
        {
            
            await TryWriteAsync(characterId, doc, cut, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
        return cut;
    }

    private async Task<VehicleDocument?> ReadAsync(uint characterId, CancellationToken ct)
        => await _docs.Find(d => d.Id == characterId).FirstOrDefaultAsync(ct);

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
            var doc = await ReadAsync(characterId, ct);
            var next = change(doc?.Vehicles ?? new List<OwnedVehicle>());
            if (next is null)
                return null;
            if (await TryWriteAsync(characterId, doc, next, ct))
                return next;
        }
        throw new InvalidOperationException(
            $"The vehicle list of character {characterId} kept changing; gave up after {MaxAttempts} attempts.");
    }

 /// <summary>
 /// Stores <paramref name="next"/> if the document is still the one <paramref name="read"/> came from (insert when
 /// there was none). False when another writer changed it first.
 /// </summary>
    private async Task<bool> TryWriteAsync(uint characterId, VehicleDocument? read, IReadOnlyList<OwnedVehicle> next, CancellationToken ct)
    {
        
        var now = DateTime.UtcNow;
        var stamp = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        if (read is not null && stamp <= read.UpdatedUtc)
            stamp = read.UpdatedUtc.AddMilliseconds(1);
        var doc = new VehicleDocument { Id = characterId, Vehicles = next.ToList(), UpdatedUtc = stamp };

        if (read is null)
        {
            try
            {
                await _docs.InsertOneAsync(doc, cancellationToken: ct);
                return true;
            }
            catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                return false; 
            }
        }

        var readStamp = read.UpdatedUtc;
        var result = await _docs.ReplaceOneAsync(d => d.Id == characterId && d.UpdatedUtc == readStamp, doc, cancellationToken: ct);
        return result.MatchedCount == 1;
    }
}
