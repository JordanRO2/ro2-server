using RO2.Economy.Domain;
using MongoDB.Driver;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Economy.Infrastructure.Mongo;

/// <summary>
/// MongoDB store for <see cref="IPrivateShopCraftRepository"/>, one document per character in the
/// <c>private_shop_crafts</c> collection. Self-contained like <see cref="CraftRecipeRepository"/>: raw document I/O
/// over the process-wide <see cref="MongoDbContext"/>.
/// </summary>
/// <remarks>
/// Offers are stored per character, since each character has its own list. The offers are profession recipes, which
/// <see cref="CraftRecipeRepository"/> also keeps per character.
/// The list is kept in insertion order and saved whole (<see cref="ReplaceOffersAsync"/> replaces the document).
/// No size cap is enforced in storage.
/// </remarks>
public sealed class PrivateShopCraftRepository : IPrivateShopCraftRepository
{
    private const string CollectionName = "private_shop_crafts";

 /// <summary>One character's offers. <c>Id</c> maps to Mongo <c>_id</c> = the character id.</summary>
    private sealed class PrivateShopCraftDocument
    {
        public uint Id { get; set; }

 /// <summary>The offers in list order, the order 2646 and 2658 carry them in.</summary>
        public List<PrivateShopCraftEntry> Offers { get; set; } = [];

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

 /// <summary>One stored offer: a Craft_ItemList id and the price of one run. Internal (not private) only so
 /// the tests can reach <see cref="ToEntries"/> and <see cref="FromEntries"/>.</summary>
    internal sealed class PrivateShopCraftEntry
    {
        public ushort CraftId { get; set; }

 /// <summary>
 /// The price as its u64 bit pattern. Stored as a long because the driver cannot write a UInt64 above
 /// long.MaxValue; the client's price input is capped at 99,999,999.
 /// </summary>
        public long Price { get; set; }
    }

    private readonly IMongoCollection<PrivateShopCraftDocument> _docs;

    /// <summary>Creates the repository over the given database context.</summary>
    public PrivateShopCraftRepository(MongoDbContext context)
    {
        _docs = context.GetCollection<PrivateShopCraftDocument>(CollectionName);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PrivateShopCraftOffer>> GetOffersAsync(uint characterId, CancellationToken ct = default)
    {
        var doc = await _docs.Find(d => d.Id == characterId).FirstOrDefaultAsync(ct);
        return doc is null ? Array.Empty<PrivateShopCraftOffer>() : FromEntries(doc.Offers);
    }

    /// <inheritdoc />
    public Task ReplaceOffersAsync(uint characterId, IReadOnlyList<PrivateShopCraftOffer> offers, CancellationToken ct = default) =>
        _docs.ReplaceOneAsync(d => d.Id == characterId,
            new PrivateShopCraftDocument { Id = characterId, Offers = ToEntries(offers), UpdatedAt = DateTime.UtcNow },
            new ReplaceOptions { IsUpsert = true }, ct);

 /// <summary>The stored form of a list, with each price kept as a long (see <see cref="PrivateShopCraftEntry.Price"/>).</summary>
    internal static List<PrivateShopCraftEntry> ToEntries(IEnumerable<PrivateShopCraftOffer> offers) =>
        offers.Select(o => new PrivateShopCraftEntry { CraftId = o.CraftId, Price = unchecked((long)o.Price) }).ToList();

 /// <summary>The offers of a stored list, in its order.</summary>
    internal static List<PrivateShopCraftOffer> FromEntries(IEnumerable<PrivateShopCraftEntry>? entries) =>
        entries is null
            ? new List<PrivateShopCraftOffer>()
            : entries.Select(e => new PrivateShopCraftOffer(e.CraftId, unchecked((ulong)e.Price))).ToList();
}
