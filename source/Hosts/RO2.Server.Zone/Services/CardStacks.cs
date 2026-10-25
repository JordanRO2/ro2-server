using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Decides which bag stack a card entering the bag one at a time may join. A card joins a stack only when both would carry the same record: same card, same enforce grade and same bind state.
/// </summary>
/// <remarks>
/// <para>
/// Cards of different enforce grades never share a stack, and a card is never merged into a stack of the other bind state when the bind flag is per row (item bind type 1).
/// </para>
/// <para>
/// The stack cap is the item's <c>Stack_Max</c>. A stack with a cap of 1 never stacks. When several stacks fit, the lowest flat slot is used.
/// </para>
/// </remarks>
internal static class CardStacks
{
 /// <summary>
 /// Finds a placed bag stack of <paramref name="cardId"/> at enforce grade <paramref name="grade"/> that one more card with bind state <paramref name="bound"/> may join.
 /// </summary>
 /// <returns>The stack, or null when none fits and the card needs a new cell.</returns>
 /// <param name="owned">Everything the character owns; only placed bag rows are considered.</param>
 /// <param name="template">The card's item template (<c>Stack_Max</c> and <c>Bind_Type</c>); null never stacks.</param>
    public static InventoryItem? FindJoinStack(IEnumerable<InventoryItem> owned, ItemData? template, uint cardId,
        byte grade, bool bound)
    {
        if (template is null || template.Stack_Max <= 1) return null;
        byte wanted = WorldEntryService.RecordBindFlags(template.Bind_Type, 0, bound);
        return owned
            .Where(i => i.StorageType == 0 && i.Slot >= 0 && i.ItemId == cardId && i.Refine == grade
                        && i.Count > 0 && i.Count + 1 <= template.Stack_Max
                        && WorldEntryService.RecordBindFlags(template.Bind_Type, 0, i.Bound) == wanted)
            .OrderBy(i => i.Slot)
            .FirstOrDefault();
    }
}
