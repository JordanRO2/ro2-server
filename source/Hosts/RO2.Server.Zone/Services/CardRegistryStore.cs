using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RO2.Domain.Entities;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Registry of every card a character has registered. It backs the card book list, which keeps cards listed even when no copies are held.
/// </summary>
/// <remarks>
/// <para>
/// Cards are stored with a state: <see cref="StateNew"/> for a newly registered card, <see cref="StateSeen"/> once the player has put it into the book.
/// </para>
/// <para>
/// Only cards whose item type is <see cref="ListedCardItemType"/> are stored. Persistence goes through <see cref="RO2.Application.Interfaces.ICardRegistryRepository"/>.
/// </para>
/// <para>
/// <see cref="CardRegistrationNotifier.NotifyArrivedAsync"/> registers cards as they reach the bag. <see cref="Merge"/> reconciles the registry with held and booked cards on each map load.
/// </para>
/// </remarks>
public static class CardRegistryStore
{
 /// <summary>Registry state of a card that has been registered but not yet viewed by the player.</summary>
    public const byte StateNew = 2;

 /// <summary>Registry state of a card the player has seen.</summary>
    public const byte StateSeen = 1;

 /// <summary>The item type of cards that appear in the card book list.</summary>
    public const byte ListedCardItemType = 13;

 /// <summary>
 /// Adds the cards in the book and the listable cards the character holds to <paramref name="registry"/>.
 /// Cards in the book are marked <see cref="StateSeen"/>; newly found held cards are marked <see cref="StateNew"/>. Entries are never removed.
 /// </summary>
 /// <returns>True if the registry changed.</returns>
    public static bool Merge(List<CardRegistryEntry> registry, IEnumerable<uint> cardsInBook, IEnumerable<uint> cardsHeld)
    {
        var byId = new Dictionary<uint, CardRegistryEntry>(registry.Count);
        foreach (var e in registry) byId.TryAdd(e.CardId, e);
        bool changed = false;

        foreach (var cardId in cardsInBook)
        {
            if (byId.TryGetValue(cardId, out var e))
            {
                if (e.State != StateSeen) { e.State = StateSeen; changed = true; }
                continue;
            }
            e = new CardRegistryEntry { CardId = cardId, State = StateSeen };
            byId[cardId] = e;
            registry.Add(e);
            changed = true;
        }

        foreach (var cardId in cardsHeld)
        {
            if (byId.ContainsKey(cardId)) continue;
            var e = new CardRegistryEntry { CardId = cardId, State = StateNew };
            byId[cardId] = e;
            registry.Add(e);
            changed = true;
        }
        return changed;
    }
}
