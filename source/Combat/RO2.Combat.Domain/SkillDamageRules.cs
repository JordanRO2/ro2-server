namespace RO2.Combats.Domain;

/// <summary>
/// Pure rules over the Skill_Table_SkillLevel damage columns: how many records one 2718 NPC element carries, how
/// many NPCs a cast may damage, and how a cast's full damage splits across its records. Operates on rows passed
/// in; the host owns the table load (the ISkillCatalog adapter, B4 S8).
/// </summary>
public static class SkillDamageRules
{
    /// <summary>Two Skill_Table_SkillLevel columns of one row (0 = not yet raised to 1).</summary>
    public readonly record struct SkillLevelRow(int NormalInfluences, int DamageQuantity);

    /// <summary>
    /// How many damage records each NPC element of this skill's 2718 carries: <c>max(1, NormalInfluences)</c>, or
    /// 1 for a skill the table does not know (wire-visible: it is the 2718 element count).
    /// </summary>
    public static int NormalInfluencesOf(IReadOnlyDictionary<uint, SkillLevelRow> rows, uint skillId)
        => rows.TryGetValue(skillId, out var row) ? Math.Max(1, row.NormalInfluences) : 1;

    /// <summary>The most NPCs one cast may damage: <c>max(1, DamageQuantity)</c>, or null for an unknown skill.</summary>
    public static int? DamageQuantityOf(IReadOnlyDictionary<uint, SkillLevelRow> rows, uint skillId)
        => rows.TryGetValue(skillId, out var row) ? Math.Max(1, row.DamageQuantity) : null;

    /// <summary>
    /// One damage record's amount when a cast's full damage is split over <paramref name="records"/>:
    /// <c>max(1, round(full / records, AwayFromZero))</c>, so the element's records together carry the rate once.
    /// </summary>
    public static int RecordDamage(int fullDamage, int records)
        => Math.Max(1, (int)Math.Round((double)fullDamage / Math.Max(1, records), MidpointRounding.AwayFromZero));
}
