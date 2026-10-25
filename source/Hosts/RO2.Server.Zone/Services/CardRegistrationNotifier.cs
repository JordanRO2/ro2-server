using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Sends the card registration notice (2588 AnsRegistCardBook) when a card reaches the player's bag, and records listable cards in <see cref="CardRegistryStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every path that adds a card to the bag calls <see cref="NotifyArrivedAsync"/> after its item updates. World entry calls <see cref="HeldMvpCards"/> instead.
/// </para>
/// <para>
/// Listable cards (Item_Type 13) are announced only the first time the character obtains them. MVP cards (Item_Type 139) are announced on every arrival and are not stored. Each card gets one notice.
/// </para>
/// <para>
/// A concurrent book list request can drop a newly added entry. The next book list request registers the card again from the bag, so the card is never lost from the book.
/// </para>
/// </remarks>
internal static class CardRegistrationNotifier
{
 /// <summary>Item type of the MVP cards. These are announced on every arrival and never listed.</summary>
    internal const byte MvpCardItemType = 139;

 /// <summary>
 /// Gets the cards among the arrived items that need a registration notice, in arrival order, each once.
 /// </summary>
 /// <remarks>
 /// A listable card that the registry does not hold is added to it as new. An MVP card is announced without changing the registry.
 /// </remarks>
 /// <param name="registry">Card registry entries of the character.</param>
 /// <param name="arrivedItemIds">Item ids that reached the bag.</param>
 /// <param name="cardItemTypeOf">Gets an item's Item_Type, or null for an item that is not a card.</param>
    internal static List<uint> CardsToAnnounce(List<CardRegistryEntry> registry, IEnumerable<uint> arrivedItemIds,
        Func<uint, byte?> cardItemTypeOf, out bool registryChanged)
    {
        registryChanged = false;
        var registered = new HashSet<uint>(registry.Select(e => e.CardId));
        var announced = new HashSet<uint>();
        var announce = new List<uint>();
        foreach (var itemId in arrivedItemIds)
        {
            if (itemId == 0 || announced.Contains(itemId)) continue;
            switch (cardItemTypeOf(itemId))
            {
                case MvpCardItemType:
                    break;
                case CardRegistryStore.ListedCardItemType when registered.Add(itemId):
                    registry.Add(new CardRegistryEntry { CardId = itemId, State = CardRegistryStore.StateNew });
                    registryChanged = true;
                    break;
                default:
                    continue;   
            }
            announced.Add(itemId);
            announce.Add(itemId);
        }
        return announce;
    }

 /// <summary>
 /// Gets the distinct MVP cards in the bag rows, in row order. These are announced at world entry.
 /// </summary>
 /// <remarks>
 /// Cards of Item_Type 13 are not announced at world entry. They are registered by the next book list request.
 /// </remarks>
 /// <param name="items">Inventory rows.</param>
 /// <param name="store">Game data store.</param>
    internal static List<uint> HeldMvpCards(IEnumerable<InventoryItem> items, GameDataStore store)
    {
        var cards = new List<uint>();
        foreach (var it in items)
        {
            if (it.StorageType != 0 || it.Count <= 0 || cards.Contains(it.ItemId)) continue;
            if (CardItemTypeOf(store, it.ItemId) == MvpCardItemType) cards.Add(it.ItemId);
        }
        return cards;
    }

 /// <summary>
 /// Sends the registration notices for items that just reached the character's bag, after the registry is updated.
 /// </summary>
 /// <remarks>
 /// Storage errors do not throw. If the registry cannot be read, only MVP cards are announced. If it cannot be saved, the notices are still sent.
 /// </remarks>
 /// <param name="session">Session to send the notices on.</param>
 /// <param name="characterId">Character that received the items.</param>
 /// <param name="arrivedItemIds">Item ids that reached the bag.</param>
    public static async Task NotifyArrivedAsync(ProudSession session, uint characterId, IEnumerable<uint> arrivedItemIds,
        IServiceProvider services, ILogger logger)
    {
        if (characterId == 0) return;
        var store = services.GetRequiredService<GameDataStore>();
        byte? TypeOf(uint itemId) => CardItemTypeOf(store, itemId);

        var cards = arrivedItemIds.Where(id => TypeOf(id) is not null).Distinct().ToList();
        if (cards.Count == 0) return;

        List<uint> announce;
        if (!cards.Any(id => TypeOf(id) == CardRegistryStore.ListedCardItemType))
        {
            announce = CardsToAnnounce(new List<CardRegistryEntry>(), cards, TypeOf, out _);
        }
        else
        {
            ICardRegistryRepository registryStore;
            CardRegistryDocument doc;
            try
            {
                registryStore = services.GetRequiredService<ICardRegistryRepository>();
                doc = await registryStore.GetAsync(characterId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Card registry load failed for char {Char}; 2588 only for MVP cards", characterId);
                announce = CardsToAnnounce(new List<CardRegistryEntry>(), cards.Where(id => TypeOf(id) == MvpCardItemType),
                    TypeOf, out _);
                await SendAsync(session, announce);
                return;
            }

            announce = CardsToAnnounce(doc.Cards, cards, TypeOf, out bool changed);
            if (changed)
            {
                try { await registryStore.SaveAsync(doc); }
                catch (Exception ex) { logger.LogWarning(ex, "Card registry save failed for char {Char}", characterId); }
            }
        }

        await SendAsync(session, announce);
        if (announce.Count > 0)
            logger.LogDebug("Char {Char}: 2588 for card(s) {Cards}", characterId, string.Join(", ", announce));
    }

    private static async Task SendAsync(ProudSession session, IEnumerable<uint> cards)
    {
        foreach (var cardId in cards)
            await session.SendRmiAsync(ZoneS2COpcodes.AnsRegistCardBook, CardPackets.BuildRegistCardBook2588(cardId));
    }

 /// <summary>Gets the Item_Type of a card, or null when the item is not a card.</summary>
 /// <param name="store">Game data store.</param>
 /// <param name="itemId">Item id.</param>
    private static byte? CardItemTypeOf(GameDataStore store, uint itemId)
        => itemId != 0 && CardIds(store).Contains(itemId) ? store.GetItem(itemId)?.Item_Type : null;

 /// <summary>Card ids from CardInfo, built once for each loaded store.</summary>
    private static readonly ConditionalWeakTable<GameDataStore, HashSet<uint>> CardIdsByStore = new();

    private static HashSet<uint> CardIds(GameDataStore store)
        => CardIdsByStore.GetValue(store, s => s.CardInfo.Select(c => c.Card_ID).ToHashSet());
}
