using RO2.GameData.Tables;
using RO2.Worlds.Domain;

namespace RO2.Worlds.Application;

/// <summary>
/// The offering rule of a 1693 ReqMakeOfferings: which stacks may be offered, what is left of a stack after an offering, and
/// which acknowledgement string an offering earns. Pure: the host reads and writes the inventory, sends every message and
/// counts the offering in the world event in its own original order.
/// </summary>
public static class OfferingUseCase
{
    /// <summary>Inventory storage type of the character's bag; only a bag stack can be offered.</summary>
    public const byte BagStorageType = 0;

    /// <summary>
    /// Whether a stack may be offered: it belongs to the offering character, sits in the bag and holds at least
    /// <paramref name="count"/> items. A request that fails is dropped silently.
    /// </summary>
    public static bool CanOffer(uint stackCharacterId, byte stackStorageType, int stackCount, uint characterId, byte count)
        => stackCharacterId == characterId && stackStorageType == BagStorageType && stackCount >= count;

    /// <summary>
    /// What is left of a stack of <paramref name="stackCount"/> after <paramref name="count"/> are offered; the stack is removed
    /// when nothing is left (the remainder is then 0, never negative).
    /// </summary>
    public static (int Remaining, bool RemoveStack) After(int stackCount, byte count)
    {
        var remaining = stackCount - count;
        return remaining <= 0 ? (0, true) : (remaining, false);
    }

    /// <summary>
    /// The acknowledgement string of offering item <paramref name="itemId"/> to NPC <paramref name="npcId"/>: the Type_Data_4 of
    /// the first matching "offer item X to NPC Y" row of WorldEvent_Check_Group, or 0 when no row matches.
    /// </summary>
    public static ushort MessageStringId(IEnumerable<WorldEventCheckGroup> checks, uint npcId, uint itemId)
    {
        var row = checks.FirstOrDefault(r =>
            r.Check_Type == WorldEventTables.CheckTypeOffering && r.Type_Data_1 == npcId && r.Type_Data_2 == itemId);
        return row is null ? (ushort)0 : row.Type_Data_4;
    }
}
