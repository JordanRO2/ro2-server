using RO2.Economy.Domain;
using MongoDB.Driver;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Economy.Infrastructure.Mongo;

/// <summary>
/// MongoDB implementation of <see cref="IGameMallListRepository"/>, owning the <c>game_mall_lists</c>
/// collection (one document per account, keyed by account id). The order and duplicate rules are in the
/// interface remarks.
/// </summary>
public sealed class GameMallListRepository : IGameMallListRepository
{
 /// <summary>One account's game-mall item list 2. <c>Id</c> maps to Mongo <c>_id</c> = the account id.</summary>
    private sealed class GameMallListDocument
    {
        public uint Id { get; set; }
        public List<uint> Items { get; set; } = new();
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    private readonly IMongoCollection<GameMallListDocument> _docs;

    /// <summary>Creates the repository over the given database context.</summary>
    public GameMallListRepository(MongoDbContext context)
    {
        _docs = context.GetCollection<GameMallListDocument>("game_mall_lists");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<uint>> GetAsync(uint accountId, CancellationToken ct = default)
    {
        var doc = await _docs.Find(d => d.Id == accountId).FirstOrDefaultAsync(ct);
        return doc?.Items ?? (IReadOnlyList<uint>)Array.Empty<uint>();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<uint>> AddAsync(uint accountId, IReadOnlyList<uint> ids, CancellationToken ct = default)
    {
        var doc = await _docs.Find(d => d.Id == accountId).FirstOrDefaultAsync(ct)
                  ?? new GameMallListDocument { Id = accountId };
        var next = Prepend(doc.Items, ids);
        if (next.Count == doc.Items.Count) return doc.Items;

        doc.Items = next;
        doc.UpdatedAt = DateTime.UtcNow;
        await _docs.ReplaceOneAsync(d => d.Id == accountId, doc, new ReplaceOptions { IsUpsert = true }, ct);
        return doc.Items;
    }

 /// <summary>
 /// The list after adding <paramref name="ids"/> one at a time to the front of <paramref name="current"/>,
 /// skipping any id already in it. Pure, so the order rule is testable without a database.
 /// </summary>
    public static List<uint> Prepend(IReadOnlyList<uint> current, IReadOnlyList<uint> ids)
    {
        var result = new List<uint>(current);
        foreach (var id in ids)
        {
            if (result.Contains(id)) continue;
            result.Insert(0, id);
        }
        return result;
    }
}
