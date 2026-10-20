using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Persistence.Mongo.Repositories;

/// <summary>
/// MongoDB store for <see cref="IPetIncubatorRepository"/>, one document per character in the <c>pet_incubators</c>
/// collection. Each change is a read-modify-write of the whole document, serialized per character by the Zone
/// host's request lock. Stateless apart from its collection handle, so it is registered as a singleton.
/// </summary>
public sealed class PetIncubatorRepository : IPetIncubatorRepository
{
    private const string CollectionName = "pet_incubators";

 /// <summary>A character's incubator slots. <see cref="Id"/> maps to Mongo <c>_id</c> = the character id.</summary>
    private sealed class PetIncubatorDocument
    {
        public uint Id { get; set; }
        public List<PetIncubatorSlot> Slots { get; set; } = new();
    }

    private readonly IMongoCollection<PetIncubatorDocument> _docs;

    public PetIncubatorRepository(MongoDbContext context)
    {
        _docs = context.GetCollection<PetIncubatorDocument>(CollectionName);
    }

    public async Task<IReadOnlyList<PetIncubatorSlot>> GetSlotsAsync(uint characterId, CancellationToken ct = default)
        => (await LoadAsync(characterId, ct)).Slots;

    public async Task<bool> TryPlaceIncubatorAsync(uint characterId, byte slot, uint incubatorId, byte charges, DateTime nowUtc, CancellationToken ct = default)
    {
        var doc = await LoadAsync(characterId, ct);
        if (!PetIncubatorRules.TryPlace(doc.Slots, slot, incubatorId, charges, nowUtc)) return false;
        await SaveAsync(doc, ct);
        return true;
    }

    public async Task<bool> TryStartIncubationAsync(uint characterId, byte slot, uint incubatorId, uint eggId, DateTime startUtc, DateTime endUtc, CancellationToken ct = default)
    {
        var doc = await LoadAsync(characterId, ct);
        if (!PetIncubatorRules.TryStartIncubation(doc.Slots, slot, incubatorId, eggId, startUtc, endUtc)) return false;
        await SaveAsync(doc, ct);
        return true;
    }

    public async Task<PetIncubatorSlot?> TryCollectAsync(uint characterId, byte slot, uint incubatorId, DateTime nowUtc, CancellationToken ct = default)
    {
        var doc = await LoadAsync(characterId, ct);
        var s = PetIncubatorRules.CollectableSlot(doc.Slots, slot, incubatorId, nowUtc);
        if (s == null) return null;
        var before = PetIncubatorRules.Copy(s);
        PetIncubatorRules.ApplyCollect(doc.Slots, s, nowUtc);
        await SaveAsync(doc, ct);
        return before;
    }

    public async Task RestoreSlotAsync(uint characterId, PetIncubatorSlot before, CancellationToken ct = default)
    {
        var doc = await LoadAsync(characterId, ct);
        PetIncubatorRules.RestoreSlot(doc.Slots, before);
        await SaveAsync(doc, ct);
    }

    private async Task<PetIncubatorDocument> LoadAsync(uint characterId, CancellationToken ct)
        => await _docs.Find(d => d.Id == characterId).FirstOrDefaultAsync(ct)
           ?? new PetIncubatorDocument { Id = characterId };

    private Task SaveAsync(PetIncubatorDocument doc, CancellationToken ct)
        => _docs.ReplaceOneAsync(d => d.Id == doc.Id, doc, new ReplaceOptions { IsUpsert = true }, ct);
}
