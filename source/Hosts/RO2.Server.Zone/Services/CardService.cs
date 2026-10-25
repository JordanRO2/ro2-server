using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Persistence.Mongo.Persistence;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Business logic for the card book: registering, unregistering and listing cards, stored through <see cref="ICardBookRepository"/>.
/// </summary>
/// <remarks>
/// <para>
/// Item moves go through <see cref="IInventoryRepository"/>. Card composition is handled by the composition handler, not by this service.
/// </para>
/// <para>
/// The service is not registered in DI. Handlers create it with <see cref="From"/>, passing the scoped services they already hold.
/// </para>
/// <para>
/// Storage errors are logged and swallowed, so a database failure does not take the Zone server down.
/// </para>
/// <para>
/// A card is never duplicated: when registered, its inventory row is deleted before the book entry is written, and when taken off, the book entry is removed before the item is re-created.
/// </para>
/// </remarks>
public sealed class CardService
{
    private readonly ICardBookRepository _repo;
    private readonly GameDataStore _data;
    private readonly IInventoryRepository _inventory;
    private readonly ILogger _log;

    
    
    private readonly Dictionary<uint, byte> _cardGrade;

    public CardService(ICardBookRepository repo, GameDataStore data, IInventoryRepository inventory, ILogger log)
    {
        _repo = repo;
        _data = data;
        _inventory = inventory;
        _log = log;

        _cardGrade = new Dictionary<uint, byte>();
        foreach (var c in data.CardInfo)
            _cardGrade[c.Card_ID] = c.Grade_Type;
    }

 /// <summary>Creates a <see cref="CardService"/> from a handler scope and logger.</summary>
    public static CardService From(IServiceProvider services, ILogger log) =>
        new CardService(
            services.GetRequiredService<ICardBookRepository>(),
            services.GetRequiredService<GameDataStore>(),
            services.GetRequiredService<IInventoryRepository>(),
            log);

 /// <summary>Returns true if <paramref name="itemId"/> is a card.</summary>
    public bool IsCard(uint itemId) => _cardGrade.ContainsKey(itemId);

 /// <summary>Returns the grade of a card, or 0 if it is unknown.</summary>
    public byte GradeOf(uint cardId) => _cardGrade.TryGetValue(cardId, out var g) ? g : (byte)0;

    
 /// <summary>Loads the character's card book, or returns an empty one if it cannot be read.</summary>
    public async Task<CardBookDocument> GetBookAsync(uint characterId, CancellationToken ct = default)
    {
        try
        {
            return await _repo.GetOrCreateAsync(characterId, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Card: book load failed for char {Char}; using empty book", characterId);
            return new CardBookDocument { Id = characterId };
        }
    }

    private async Task SaveBookAsync(CardBookDocument doc, CancellationToken ct)
    {
        try { await _repo.SaveAsync(doc, ct); }
        catch (Exception ex) { _log.LogWarning(ex, "Card: book save failed for char {Char}", doc.Id); }
    }

    
 /// <summary>
 /// Registers the inventory stack <paramref name="itemUid"/> into the character's card book after checking ownership.
 /// The stack must exist, belong to <paramref name="characterId"/>, and contain cards. On success the stack's row is removed from the bag and a book entry is written for each card in the stack.
 /// </summary>
 /// <returns>
 /// <c>ok</c> is false when nothing changed. Otherwise <c>cardId</c> and <c>enforceGrade</c> identify the card and its enforce grade, and <c>copiesInBook</c> is the number of copies of that card at that grade now in the book.
 /// </returns>
 /// <remarks>
 /// The whole stack is registered, not a single card from it. Entries keep the stack's enforce grade, and copy counts are tracked per card and enforce grade.
 /// </remarks>
    public async Task<(bool ok, uint cardId, byte enforceGrade, int copiesInBook)> PutCardAsync(uint characterId, ulong itemUid, byte kind, CancellationToken ct = default)
    {
        try
        {
            if (itemUid == 0) return (false, 0, 0, 0);
            var item = await _inventory.GetByIdAsync(itemUid);
            if (item is null || item.CharacterId != characterId) return (false, 0, 0, 0);  
            if (!IsCard(item.ItemId)) return (false, 0, 0, 0);                              

            var cardId = item.ItemId;
            var grade = GradeOf(cardId);
            
            
            var enforceGrade = item.Refine;
            int stackCount = Math.Max(item.Count, 1);   

            
            
            await _inventory.RemoveItemAsync(itemUid);

            var book = await GetBookAsync(characterId, ct);
            for (int i = 0; i < stackCount; i++)
                book.Cards.Add(new CardBookEntry { CardId = cardId, Grade = grade, EnforceGrade = enforceGrade, Kind = kind, OriginItemUid = itemUid });
            await SaveBookAsync(book, ct);

            return (true, cardId, enforceGrade,
                book.Cards.Count(e => e.CardId == cardId && e.EnforceGrade == enforceGrade && e.Kind == kind));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Card: put failed for char {Char} uid {Uid}", characterId, itemUid);
            return (false, 0, 0, 0);
        }
    }

 /// <summary>
 /// Removes the first book entry matching <paramref name="cardId"/> at <paramref name="enforceGrade"/> and re-creates the card in the bag at that grade.
 /// The entry is removed before the item is created.
 /// </summary>
 /// <returns>True if a matching entry existed.</returns>
 /// <remarks>
 /// The enforce grade is part of the lookup key and of the returned item. A copy of another grade is never taken instead.
 /// <para>
 /// The <paramref name="kind"/> argument is returned as the row's bind state (<see cref="TakenOffRow"/>).
 /// </para>
 /// </remarks>
    public async Task<bool> TakeOffAsync(uint characterId, uint cardId, byte enforceGrade, byte kind, CancellationToken ct = default)
    {
        try
        {
            var book = await GetBookAsync(characterId, ct);
            var idx = book.Cards.FindIndex(e => e.CardId == cardId && e.EnforceGrade == enforceGrade && e.Kind == kind);
            if (idx < 0) return false;
            book.Cards.RemoveAt(idx);
            await SaveBookAsync(book, ct);

            
            await _inventory.AddItemAsync(TakenOffRow(characterId, cardId, enforceGrade, kind));
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Card: take-off failed for char {Char} card {Card} +{Grade}", characterId, cardId, enforceGrade);
            return false;
        }
    }

 /// <summary>
 /// Builds the bag row that a take-off re-creates for one book copy of <paramref name="cardId"/>: one card at the copy's enforce grade, unplaced, and bound when the copy's kind is 1.
 /// </summary>
 /// <remarks>
 /// The kind is the copy's bind flag. For cards with a bind type of 0 or 2 the bind flag comes from the item template instead of the kind.
 /// </remarks>
    internal static InventoryItem TakenOffRow(uint characterId, uint cardId, byte enforceGrade, byte kind) => new()
    {
        CharacterId = characterId,
        ItemId = cardId,
        Count = 1,
        Refine = enforceGrade,
        Bound = kind != 0,
        StorageType = 0,
        Slot = -1,
    };
}
