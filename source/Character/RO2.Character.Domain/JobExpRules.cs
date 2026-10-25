using RO2.GameData;

namespace RO2.Characters.Domain;

/// <summary>
/// Pure job (profession) experience/level rules over the exp table: apply a gain, the table's top level, and the
/// exp that reaches a target level. Thresholds come from the Base_Exp_Next column. Level 50 is the cap (remainder
/// dropped there). The host owns the grant flow (sending/persisting); this is the math only.
/// </summary>
public static class JobExpRules
{
    /// <summary>The job level and in-level exp after a gain.</summary>
    public readonly record struct Result(byte Level, ulong Exp, bool LevelledUp);

    /// <summary>
    /// Apply <paramref name="gain"/> to (<paramref name="level"/>, <paramref name="exp"/>): while the exp reaches
    /// the level's threshold, subtract it and go up one level. At the table's top level the exp is 0.
    /// </summary>
    public static Result Apply(byte level, ulong exp, ulong gain, IReadOnlyDictionary<byte, ExpTableEntry> table)
    {
        byte cap = MaxLevel(table);
        byte start = level;
        ulong total = exp > ulong.MaxValue - gain ? ulong.MaxValue : exp + gain;
        while (level < cap && table.TryGetValue(level, out var row) && row.Base_Exp_Next > 0
               && total >= (ulong)row.Base_Exp_Next)
        {
            total -= (ulong)row.Base_Exp_Next;
            level++;
        }
        if (level >= cap) total = 0;
        return new Result(level, total, level > start);
    }

    /// <summary>The highest level of the exp table, 50 on the shipped data.</summary>
    public static byte MaxLevel(IReadOnlyDictionary<byte, ExpTableEntry> table) =>
        table.Count == 0 ? (byte)0 : table.Keys.Max();

    /// <summary>
    /// The exp that takes (<paramref name="level"/>, <paramref name="exp"/>) exactly to <paramref name="target"/>
    /// with 0 left over: the thresholds of <paramref name="level"/> .. <paramref name="target"/> - 1, less the
    /// exp already held. 0 when the target is not above the level or a threshold row is missing.
    /// </summary>
    public static ulong ExpToReach(byte level, ulong exp, byte target, IReadOnlyDictionary<byte, ExpTableEntry> table)
    {
        if (target <= level) return 0;
        ulong sum = 0;
        for (byte l = level; l < target; l++)
        {
            if (!table.TryGetValue(l, out var row) || row.Base_Exp_Next <= 0) return 0;
            sum += (ulong)row.Base_Exp_Next;
        }
        return sum > exp ? sum - exp : 0;
    }
}
