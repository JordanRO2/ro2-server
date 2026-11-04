using RO2.Economy.Domain;
using MongoDB.Driver;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Economy.Infrastructure.Mongo;

/// <summary>
/// MongoDB implementation of <see cref="ICraftRecipeRepository"/>, storing each character's learned
/// crafting recipes in the <c>craft_recipes</c> collection (one document per character, keyed by character
/// id). The recipe list sent to the client (2535/2538) is built by the Zone-side helper from these ids plus
/// the profession's base recipes; this store holds only the learned set.
/// </summary>
public sealed class CraftRecipeRepository : ICraftRecipeRepository
{
    private const string CollectionName = "craft_recipes";

 /// <summary>One character's learned recipes. <c>Id</c> maps to Mongo <c>_id</c> = the character id.</summary>
    private sealed class CraftRecipeDocument
    {
        public uint Id { get; set; }
        public List<ushort> Recipes { get; set; } = [];
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    private readonly IMongoCollection<CraftRecipeDocument> _docs;

    /// <summary>Creates the repository over the given database context.</summary>
    public CraftRecipeRepository(MongoDbContext context)
    {
        _docs = context.GetCollection<CraftRecipeDocument>(CollectionName);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ushort>> GetAsync(uint characterId, CancellationToken ct = default)
    {
        var doc = await _docs.Find(d => d.Id == characterId).FirstOrDefaultAsync(ct);
        return doc?.Recipes ?? (IReadOnlyList<ushort>)Array.Empty<ushort>();
    }

    /// <inheritdoc />
    public async Task<bool> AddAsync(uint characterId, ushort recipeId, CancellationToken ct = default)
    {
        // Filter on the recipe being absent so an already-learned recipe matches nothing: the upsert then collides on _id
        // (DuplicateKey) and reads as "already stored", like the Postgres ON CONFLICT DO NOTHING (a concurrent same-recipe
        // 1348 loses). Caveat vs Postgres' per-(char,recipe) rows: two concurrent FIRST-ever learns of DIFFERENT recipes on
        // a doc-less character both try to insert _id=characterId; one wins, the other hits DuplicateKey and is dropped
        // (benign: reported not-stored, no charge, retryable; near-unreachable since one connection serializes a char's 1348s).
        try
        {
            var result = await _docs.UpdateOneAsync(
                d => d.Id == characterId && !d.Recipes.Contains(recipeId),
                Builders<CraftRecipeDocument>.Update
                    .AddToSet(d => d.Recipes, recipeId)
                    .Set(d => d.UpdatedAt, DateTime.UtcNow),
                new UpdateOptions { IsUpsert = true }, ct);
            return result.UpsertedId is not null || result.ModifiedCount > 0;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }
}
