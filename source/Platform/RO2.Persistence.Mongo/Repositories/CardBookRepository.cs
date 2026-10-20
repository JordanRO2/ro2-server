using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Persistence.Mongo.Repositories;

/// <summary>
/// MongoDB implementation of <see cref="ICardBookRepository"/>. It takes the process-wide
/// <see cref="MongoDbContext"/> singleton (db ro2_server) and owns the <c>card_books</c> collection. The
/// unique _id index is implicit, so the per-character lookup is a primary-key hit and no extra index build
/// is needed.
/// </summary>
public sealed class CardBookRepository : ICardBookRepository
{
    private readonly IMongoCollection<CardBookDocument> _books;

    public CardBookRepository(MongoDbContext context)
    {
        _books = context.GetCollection<CardBookDocument>("card_books");
    }

    public async Task<CardBookDocument> GetOrCreateAsync(uint characterId, CancellationToken ct = default)
    {
        var doc = await _books.Find(d => d.Id == characterId).FirstOrDefaultAsync(ct);
        return doc ?? new CardBookDocument { Id = characterId };
    }

    public Task SaveAsync(CardBookDocument doc, CancellationToken ct = default) =>
        _books.ReplaceOneAsync(
            d => d.Id == doc.Id,
            doc,
            new ReplaceOptions { IsUpsert = true },
            ct);
}
