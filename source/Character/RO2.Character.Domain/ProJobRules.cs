using RO2.GameData.Tables;

namespace RO2.Characters.Domain;

/// <summary>Outcome of a profession-change request (1494).</summary>
public enum ChangeProJobOutcome
{
    /// <summary>The change is accepted.</summary>
    Applied,
    /// <summary>The target profession is 0 or not in the table.</summary>
    UnknownProfession,
    /// <summary>The target equals the current profession.</summary>
    SameProfession,
    /// <summary>The change item is absent, not the character's, not in the bag, empty, or not a change item.</summary>
    MissingItem,
}

/// <summary>Pure profession-change rules over the ProJob game-data rows and the change item.</summary>
public static class ProJobRules
{
    private const byte NoProfession = 0;
    private const byte BagStorageType = 0;

    /// <summary>
    /// The change decision, checked in order: an unknown/absent target profession; the same profession as current;
    /// a missing or ineligible change item; otherwise applied.
    /// </summary>
    public static ChangeProJobOutcome ChangeResult(IEnumerable<ProJobType> professions, IEnumerable<ProJobChange> changeItems,
        uint characterId, byte current, byte target, (uint CharacterId, byte StorageType, int Count, uint ItemId)? item)
    {
        if (target == NoProfession || !professions.Any(r => r.ID == target))
            return ChangeProJobOutcome.UnknownProfession;
        if (target == current)
            return ChangeProJobOutcome.SameProfession;
        if (item is not { } it || it.CharacterId != characterId || it.StorageType != BagStorageType || it.Count < 1
            || !changeItems.Any(r => r.ItemID == it.ItemId))
            return ChangeProJobOutcome.MissingItem;
        return ChangeProJobOutcome.Applied;
    }

    /// <summary>
    /// The (old, new) guide-quest pairs of a change from <paramref name="from"/> to <paramref name="to"/>:
    /// ProJob_Type Guide_Quest_ID1..3 column by column, skipping a column empty on either side or unchanged.
    /// </summary>
    public static List<(uint Old, uint New)> GuideQuestPairs(IEnumerable<ProJobType> professions, byte from, byte to)
    {
        var pairs = new List<(uint, uint)>(3);
        var a = professions.FirstOrDefault(r => r.ID == from);
        var b = professions.FirstOrDefault(r => r.ID == to);
        if (a is null || b is null) return pairs;
        foreach (var (o, n) in new[] { (a.Guide_Quest_ID1, b.Guide_Quest_ID1), (a.Guide_Quest_ID2, b.Guide_Quest_ID2),
                     (a.Guide_Quest_ID3, b.Guide_Quest_ID3) })
            if (o != 0 && n != 0 && o != n) pairs.Add((o, n));
        return pairs;
    }
}
