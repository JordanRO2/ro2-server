using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;
using RO2.Characters.Domain;

namespace RO2.Persistence.Mongo.Repositories;

public class QuickSlotRepository : IQuickSlotRepository
{
    private readonly IMongoCollection<CharacterQuickSlot> _slots;

    public QuickSlotRepository(MongoDbContext context)
    {
        _slots = context.GetCollection<CharacterQuickSlot>("character_quickslots");
    }

    public async Task<IReadOnlyList<CharacterQuickSlot>> GetByCharacterAsync(uint characterId)
        => await _slots.Find(s => s.CharacterId == characterId)
            .SortBy(s => s.Page).ThenBy(s => s.Slot).ToListAsync();

    public async Task SetAsync(CharacterQuickSlot e)
    {
        
        
        var filter = Builders<CharacterQuickSlot>.Filter.Where(
            s => s.CharacterId == e.CharacterId && s.Page == e.Page && s.Slot == e.Slot);
        var update = Builders<CharacterQuickSlot>.Update
            .Set(s => s.CharacterId, e.CharacterId)
            .Set(s => s.Page, e.Page)
            .Set(s => s.Slot, e.Slot)
            .Set(s => s.EntryType, e.EntryType)
            .Set(s => s.EntryId, e.EntryId)
            .Set(s => s.Amount, e.Amount)
            .Set(s => s.ItemUid, e.ItemUid)
            .SetOnInsert(s => s.Id, (ulong)DateTime.UtcNow.Ticks);
        await _slots.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });
    }

    public async Task ClearAsync(uint characterId, byte page, byte slot)
        => await _slots.DeleteOneAsync(
            s => s.CharacterId == characterId && s.Page == page && s.Slot == slot);

    public async Task SwapAsync(uint characterId, byte pageA, byte slotA, byte pageB, byte slotB)
    {
        var all = await GetByCharacterAsync(characterId);
        var a = all.FirstOrDefault(s => s.Page == pageA && s.Slot == slotA);
        var b = all.FirstOrDefault(s => s.Page == pageB && s.Slot == slotB);

        await ClearAsync(characterId, pageA, slotA);
        await ClearAsync(characterId, pageB, slotB);
        if (b is not null) { b.Page = pageA; b.Slot = slotA; await SetAsync(b); }
        if (a is not null) { a.Page = pageB; a.Slot = slotB; await SetAsync(a); }
    }
}
