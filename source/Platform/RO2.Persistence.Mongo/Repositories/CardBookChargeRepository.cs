using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Persistence.Mongo.Repositories;

/// <summary>
/// One character's card-book charge. <see cref="Id"/> maps to Mongo <c>_id</c> = the character id. The row is
/// kept once written, lapsed or not.
/// </summary>
internal sealed class CardBookChargeDocument
{
    public uint Id { get; set; }

 /// <summary>Expiry of the charge, UTC.</summary>
    public DateTime ChargedUntilUtc { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// MongoDB implementation of <see cref="ICardBookChargeRepository"/>, storing <see cref="CardBookChargeDocument"/>
/// in the <c>card_book_charges</c> collection. Stateless over the process-wide <see cref="MongoDbContext"/>, so
/// the Zone host registers it as a singleton.
/// </summary>
/// <remarks>
/// A collection of its own rather than a field on the card_books document: the card repository replaces the
/// whole card_books document, so a field it does not map would be wiped by the next card put or take-off.
/// The expiry is extended by exactly Duration_Time days in the server's LOCAL terms (every captured re-use:
/// previous f32 + 30.0, an OLE date on the retail server's local clock); only the stored value is UTC, hence
/// the local round trip in <see cref="ExtendChargeAsync"/>.
/// <see cref="ExtendChargeAsync"/> is a read-then-replace, as before: two concurrent extends of one character
/// can lose one (Phase 1 keeps that).
/// </remarks>
public sealed class CardBookChargeRepository : ICardBookChargeRepository
{
    private readonly IMongoCollection<CardBookChargeDocument> _docs;

    public CardBookChargeRepository(MongoDbContext context)
    {
        _docs = context.GetCollection<CardBookChargeDocument>("card_book_charges");
    }

    public async Task<DateTime?> GetChargedUntilUtcAsync(uint characterId, CancellationToken ct = default)
    {
        var doc = await _docs.Find(d => d.Id == characterId).FirstOrDefaultAsync(ct);
        return doc is null ? null : (DateTime?)DateTime.SpecifyKind(doc.ChargedUntilUtc, DateTimeKind.Utc);
    }

    public async Task<DateTime> ExtendChargeAsync(uint characterId, int days, DateTime nowUtc, CancellationToken ct = default)
    {
        DateTime now = nowUtc.ToLocalTime();
        DateTime? currentUtc = await GetChargedUntilUtcAsync(characterId, ct);
        DateTime? current = currentUtc?.ToLocalTime();
        DateTime from = current is { } stillCharged && stillCharged > now ? stillCharged : now;
        DateTime untilUtc = from.AddDays(days).ToUniversalTime();
        var doc = new CardBookChargeDocument { Id = characterId, ChargedUntilUtc = untilUtc, UpdatedAt = DateTime.UtcNow };
        await _docs.ReplaceOneAsync(d => d.Id == characterId, doc, new ReplaceOptions { IsUpsert = true }, ct);
        return untilUtc;
    }
}
