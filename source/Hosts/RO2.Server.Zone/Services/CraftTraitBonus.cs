using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using RO2.Domain.Entities;
using RO2.Domain.GameData.Tables;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// The craft bonus granted by a crafter's worn items: TraitInfo Trait_Type 106 (product quantity). For
/// Cooking and Alchemy recipes it raises the number of products made per craft tick.
/// </summary>
/// <remarks>
/// <para>
/// Only the gold and silver Master's Label items carry trait 106. Trait 105 (ability value bonus for crafted
/// items) is not applied here.
/// </para>
/// <para>
/// The bonus is <c>Craft_ItemNum x (1 + sum/10000)</c>, rounded down, and never below Craft_ItemNum. Materials
/// are unchanged.
/// </para>
/// </remarks>
internal static class CraftTraitBonus
{
 /// TraitBasicInfo 106 (Craft_add_item_val): product-quantity bonus, in 1/10000 of Craft_ItemNum.
    internal const byte QuantityTrait = 106;

 /// Scale of the trait value: 10000 equals a bonus of 100 percent (x2).
    internal const int TraitScale = 10000;

 /// Profession types the trait applies to: Cooking (1) and Alchemy (2).
    internal const byte ProTypeCooking = 1;
    internal const byte ProTypeAlchemy = 2;

 /// <summary>InventoryItem.StorageType of a worn item.</summary>
    private const byte Worn = 1;

    private static readonly ConditionalWeakTable<IReadOnlyList<TraitInfo>, FrozenDictionary<uint, int>> Index = new();

 /// Item id to summed trait-106 value, built once per loaded TraitInfo list.
    private static FrozenDictionary<uint, int> QuantityIndex(IReadOnlyList<TraitInfo> traits) =>
        Index.GetValue(traits, static t => t
            .Where(r => r.Trait_Kind == 1 && r.Trait_Type == QuantityTrait && r.Value > 0)
            .GroupBy(r => r.ItemID)
            .ToFrozenDictionary(g => g.Key, g => (int)g.Sum(r => r.Value)));

 /// <summary>
 /// The summed trait-106 value of the worn items (StorageType 1) in <paramref name="bag"/>; 0 when none is worn.
 /// </summary>
    internal static int WornQuantityBonus(IReadOnlyList<TraitInfo> traits, IEnumerable<InventoryItem> bag)
    {
        var index = QuantityIndex(traits);
        if (index.Count == 0) return 0;
        long sum = 0;
        foreach (var item in bag)
            if (item.StorageType == Worn && item.Count > 0 && index.TryGetValue(item.ItemId, out var value))
                sum += value;
        return (int)Math.Min(sum, int.MaxValue);
    }

 /// <summary>Whether trait 106 applies to <paramref name="recipe"/>: Cooking and Alchemy recipes only.</summary>
    internal static bool MultipliesQuantity(CraftItemList recipe) =>
        recipe.ProType_ID is ProTypeCooking or ProTypeAlchemy;

 /// <summary>
 /// Number of products one craft tick of <paramref name="recipe"/> makes, for a crafter whose worn items sum to
 /// <paramref name="wornQuantityBonus"/>. See <see cref="WornQuantityBonus"/>.
 /// </summary>
    internal static int ProductPerTick(CraftItemList recipe, int wornQuantityBonus)
    {
        int baseCount = global::RO2.Server.Zone.Handlers.Rmi.CraftItemHandler.ProductPerTick(recipe);
        if (wornQuantityBonus <= 0 || !MultipliesQuantity(recipe)) return baseCount;
        long scaled = (long)baseCount * (TraitScale + (long)wornQuantityBonus) / TraitScale;
        return (int)Math.Clamp(scaled, baseCount, int.MaxValue);
    }
}
