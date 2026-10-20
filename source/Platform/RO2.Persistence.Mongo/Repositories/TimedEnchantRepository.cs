using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Persistence.Mongo.Repositories;

/// <summary>
/// MongoDB store for <see cref="ITimedEnchantRepository"/> over the shared <c>inventory_items</c> collection. Each
/// decrement is a field-level compare-and-set on the item id, the enchant id and the seconds read, writing only the
/// seconds, so a concurrent restart or handler update between read and write is not overwritten.
/// </summary>
public sealed class TimedEnchantRepository : ITimedEnchantRepository
{
    private readonly IMongoCollection<InventoryItem> _items;

    public TimedEnchantRepository(MongoDbContext context)
        => _items = context.GetCollection<InventoryItem>("inventory_items");

    public async Task<IReadOnlyList<RunningEnchant>> GetRunningWornAsync(uint characterId, CancellationToken ct = default)
    {
        var running = await _items.Find(i => i.CharacterId == characterId && i.StorageType == 1
                && i.EnchantReinforceId != 0 && i.EnchantSeconds > 0)
            .ToListAsync(ct);
        return running.Select(i => new RunningEnchant(i.Id, i.EnchantReinforceId, i.EnchantSeconds)).ToList();
    }

    public async Task<bool> TryAdvanceTimerAsync(ulong itemId, ushort expectedEnchantId, ushort expectedSeconds, ushort newSeconds, CancellationToken ct = default)
    {
        var result = await _items.UpdateOneAsync(
            i => i.Id == itemId && i.EnchantReinforceId == expectedEnchantId && i.EnchantSeconds == expectedSeconds,
            Builders<InventoryItem>.Update.Set(i => i.EnchantSeconds, newSeconds),
            cancellationToken: ct);
        return result.ModifiedCount == 1;
    }
}
