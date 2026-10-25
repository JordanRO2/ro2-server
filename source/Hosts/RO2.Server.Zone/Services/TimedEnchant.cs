using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Domain.GameData.Tables;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Timed enchants of the Reinforce_Type 2 ItemReinforceInfo rows (blessings, whetstones, magic oils and embroideries),
/// applied by ReqAttachSocket. Defines which items accept an enchant, how it is stored on the item, and how its countdown runs.
/// </summary>
/// <remarks>
/// A new enchant replaces the current one. The timer counts down only while the item is worn by a player in the world.
/// The enchant's bonus applies while seconds remain.
/// </remarks>
public static class TimedEnchant
{
 /// <summary>Reinforce_Type of timed enchants.</summary>
    public const byte TimedReinforceType = 2;

 /// <summary>Seconds removed per server tick, and the tick period.</summary>
    public const int TickSeconds = 10;

 /// <summary>
 /// The ItemReinforceInfo row for a source item (the first row per source item).
 /// </summary>
    public static ItemReinforceInfo? RowOf(GameDataStore store, uint sourceItemId)
        => store.ItemReinforceInfo.FirstOrDefault(r => r.SourceItem_ID == sourceItemId);

 /// <summary>
 /// Whether an enchant may be applied to an item of the given template: its equip slot must be allowed by TargetItem_Parts,
 /// and its required level must be at least TargetItem_ReqLevel.
 /// </summary>
    public static bool Fits(ItemReinforceInfo row, ItemData target)
    {
        int slot = target.Equip_Slot;
        if (slot >= 32) return false; 
        return ((row.TargetItem_Parts >> slot) & 1u) != 0 && target.Require_Level >= row.TargetItem_ReqLevel;
    }

 /// <summary>
 /// Applies the enchant to the target at its full duration, replacing any current enchant.
 /// </summary>
    public static void Apply(InventoryItem target, ItemReinforceInfo row)
    {
        target.EnchantReinforceId = row.Reinforce_ID;
        target.EnchantSeconds = row.Duration;
    }

 /// <summary>True while the item's enchant still gives its bonus.</summary>
    public static bool IsLive(InventoryItem item) => item.EnchantReinforceId != 0 && item.EnchantSeconds > 0;

 /// <summary>Seconds left after one tick, never below 0.</summary>
    public static ushort AfterTick(ushort seconds) => seconds <= TickSeconds ? (ushort)0 : (ushort)(seconds - TickSeconds);
}
