namespace RO2.Domain.Entities;

/// <summary>
/// One document per character holding the registered card book. Persisted to the <c>card_books</c>
/// collection in the local dev Mongo (db ro2_server). <see cref="Id"/> maps to Mongo <c>_id</c> and is
/// the character id (uint) — the same Id-&gt;_id convention Character / CollectionDocument use, so there
/// is exactly one row per character and lookups are a primary-key hit.
/// </summary>
public sealed class CardBookDocument
{
 /// <summary>Character id. Maps to Mongo _id (one card-book doc per character).</summary>
    public uint Id { get; set; }

 /// <summary>Cards the character has registered into the book, in registration order.</summary>
    public List<CardBookEntry> Cards { get; set; } = new();
}
