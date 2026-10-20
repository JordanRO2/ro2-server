using RO2.Domain.GameData;
using RO2.Domain.GameData.Tables;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// The NPC repair price (1327 ReqRepairItem) and its refusal answer. The price uses the client's formula, so the zeny
/// the server takes matches the zeny the confirm dialog shows.
/// </summary>
/// <remarks>
/// Each worn item that is not a quiver and has missing durability costs missing * 1e-4 * grade factor * Price_Buy, with
/// a minimum of 1. The grade factor is 0.28 for grade 4, 0.212 for grade 3, 0.105 for grade 2, and 0.1 otherwise. The
/// total is reduced by the guild's repair discount, if any. Items with nothing missing are not priced.
/// </remarks>
internal static class RepairPricing
{
 /// <summary>ItemInfo.Equip_Type the client leaves out of the repair list and its price (row+544 != 18).</summary>
    internal const ushort UnrepairedEquipType = 18;

 /// <summary>The Guild_Skill_Level Skill_Category that grants the repair discount.</summary>
    internal const byte SkillCategoryRepairDiscount = 5;

 /// <summary>2336 result 20065: the handle is not an NPC.</summary>
    internal const ushort ResultNotNpc = 20065;

 /// <summary>2336 result 20066: none of the requested uids is an item the player holds.</summary>
    internal const ushort ResultNoItem = 20066;

 /// <summary>The refusal body length: a 2336 with no records.</summary>
    internal const int RefusalLength = 217;

 /// <summary>K, the per-point repair rate (1e-4).</summary>
    internal const double PerPointRate = (double)1e-4f;

 /// <summary>The grade factor of the repair price.</summary>
    internal static double GradeFactor(byte grade) => grade switch
    {
        4 => (double)0.28f,
        3 => (double)0.212f,
        2 => (double)0.105f,
        _ => (double)0.1f,
    };

 /// <summary>
 /// One item's price: 0 when the item has nothing missing or is a quiver, otherwise at least 1. A negative
 /// <paramref name="currentDurability"/> means full durability.
 /// </summary>
    internal static long ItemCost(ItemData info, int currentDurability)
    {
        if (info.Equip_Type == UnrepairedEquipType) return 0;
        int max = (int)Math.Min(info.Durability, ushort.MaxValue);
        int current = currentDurability < 0 ? max : currentDurability;
        int missing = max - current;
        if (missing <= 0) return 0;
        long cost = (long)((double)missing * PerPointRate * GradeFactor(info.Grade) * (double)info.Price_Buy);
        return cost < 1 ? 1 : cost;
    }

 /// <summary>
 /// The guild's repair discount in percent: the largest value among its learned Guild_Skill_Level rows with the repair
 /// category. 0 when none is learned.
 /// </summary>
    internal static long DiscountPercent(IEnumerable<uint> learned, IReadOnlyDictionary<uint, GuildSkillLevel> table)
    {
        long pct = 0;
        foreach (var id in learned)
        {
            if (!table.TryGetValue(id, out var row)) continue;
            if (row.Skill_Category_1 == SkillCategoryRepairDiscount) pct = Math.Max(pct, row.Skill_Value_1);
            if (row.Skill_Category_2 == SkillCategoryRepairDiscount) pct = Math.Max(pct, row.Skill_Value_2);
            if (row.Skill_Category_3 == SkillCategoryRepairDiscount) pct = Math.Max(pct, row.Skill_Value_3);
        }
        return pct;
    }

 /// <summary>Applies the guild's repair discount. The total is unchanged at 0 percent, and is otherwise reduced, with a minimum of 1.</summary>
    internal static long ApplyDiscount(long total, long pct)
    {
        if (pct <= 0) return total;
        long discounted = total - total * pct / 100;
        return discounted < 1 ? 1 : discounted;
    }

 /// <summary>
 /// The 2336 refusal: [u32 213][u16 code][u32 0][205 zero head][u16 0]. The client shows the code's message and resets
 /// the window, and reads nothing else.
 /// </summary>
    internal static byte[] BuildRepairRefusal2336(ushort code)
    {
        using var w = new PacketWriter(RefusalLength);
        w.WriteUInt32(RefusalLength - 4);                     
        w.WriteUInt16(code);                                  
        w.WriteUInt32(0);
        w.WriteZeros(RepairItemPackets.CharacterHeadLength);  
        w.WriteUInt16(0);                                     
        return w.GetWrittenSpan().ToArray();
    }
}
