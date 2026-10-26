namespace RO2.Combats.Application;

/// <summary>
/// The catalog facts the cast-path flows read, behind a port so Combat.Application does not reach the host
/// GameDataStore (or the Skill context's talent rules) directly. The host adapter answers these from
/// GameDataStore and RO2.Skills.Domain.TalentRules.
/// </summary>
public interface ISkillCatalog
{
    /// <summary>
    /// True when <paramref name="skillId"/> is an item skill (ItemInfo.Skill_ID_1 >= 2,000,000): a potion, warp /
    /// revival wing or town scroll started only through RMI 1561, never a raw 1508.
    /// </summary>
    bool IsItemSkill(uint skillId);

    /// <summary>True when the skill has a Skill_Table_SkillLevel row for the exact <paramref name="skillId"/>.</summary>
    bool HasLevelRow(uint skillId);

    /// <summary>
    /// True when <paramref name="skillGroup"/> is a talent class skill with a Talent_Table.Max_Skill_Level
    /// (RO2.Skills.Domain.TalentRules.TryGetSkillLevelLimit): the groups that must be learned to cast.
    /// </summary>
    bool HasTalentLimit(uint skillGroup);
}
