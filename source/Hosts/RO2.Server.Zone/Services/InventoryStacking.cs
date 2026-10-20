using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Adds a quantity of one item to a character's bag the way the game stacks it, and returns every stack the
/// quantity landed in, in the order they are listed.
/// </summary>
/// <remarks>
/// <para>
/// A quantity that fits into an existing stack (existing plus quantity does not exceed the stack maximum) is
/// added to that stack. A quantity that does not fit goes into new stacks of the stack maximum, full stacks
/// first; existing partial stacks are not topped up.
/// </para>
/// <para>
/// <see cref="IInventoryRepository"/>'s insert method stays a plain insert with no game data. Callers that
/// want stacking go through this class.
/// </para>
/// <para>
/// A new stack takes the template's durability. Without a template, the entity default is kept.
/// </para>
/// </remarks>
internal static class InventoryStacking
{
 /// <summary>One stack the quantity landed in.</summary>
 /// <param name="Stack">The stack, as it now stands.</param>
 /// <param name="Restacked">True when the stack already existed before this call; false when this call created it.</param>
    public readonly record struct Placed(InventoryItem Stack, bool Restacked);

 /// <summary>
 /// Stacks <paramref name="quantity"/> of <paramref name="itemId"/> into the character's bag using the
 /// template's stack maximum (1 when there is no template), and gives each new stack the template's durability.
 /// </summary>
    public static Task<IReadOnlyList<Placed>> AddAsync(IInventoryRepository inventory, uint characterId,
        uint itemId, int quantity, ItemData? template)
        => AddCoreAsync(inventory, characterId, itemId, quantity, template?.Stack_Max ?? (byte)1,
            template is null ? (short)-1 : (short)Math.Min(template.Durability, (uint)short.MaxValue));

    public static Task<IReadOnlyList<Placed>> AddAsync(IInventoryRepository inventory, uint characterId,
        uint itemId, int quantity, byte stackMax)
        => AddCoreAsync(inventory, characterId, itemId, quantity, stackMax, -1);

    private static async Task<IReadOnlyList<Placed>> AddCoreAsync(IInventoryRepository inventory, uint characterId,
        uint itemId, int quantity, byte stackMax, short newStackDurability)
    {
        var placed = new List<Placed>();
        if (quantity <= 0) return placed;
        int perStack = stackMax < 1 ? 1 : stackMax;   

        if (perStack > 1)
        {
            var bag = await inventory.GetByCharacterAsync(characterId);
            var target = bag
                .Where(i => i.StorageType == 0 && i.ItemId == itemId && i.Count > 0 && i.Count + quantity <= perStack)
                .OrderBy(i => i.Slot)
                .FirstOrDefault();
            if (target is not null)
            {
                target.Count += quantity;
                await inventory.UpdateItemAsync(target);
                placed.Add(new Placed(target, true));
                return placed;
            }
        }

        for (int remaining = quantity; remaining > 0;)
        {
            int n = Math.Min(perStack, remaining);
            var stack = new InventoryItem
            {
                CharacterId = characterId,
                ItemId = itemId,
                Count = n,
                Slot = -1,        
                StorageType = 0,  
                Durability = newStackDurability,
            };
            await inventory.AddItemAsync(stack);   
            placed.Add(new Placed(stack, false));
            remaining -= n;
        }
        return placed;
    }
}
