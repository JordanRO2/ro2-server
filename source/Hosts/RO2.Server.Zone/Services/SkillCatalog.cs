using RO2.Combats.Application;
using RO2.GameData;
using RO2.Skills.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Host adapter for <see cref="ISkillCatalog"/> over the live <see cref="GameDataStore"/>: the one place the
/// Combat cast-path flows reach the shipped skill tables and the Skill context's talent rules. This is where the
/// Combat-&gt;Skill call physically lives (<see cref="TalentRules.TryGetSkillLevelLimit"/>), so Combat.Application
/// itself references neither the store nor RO2.Skills.Domain.
/// </summary>
internal sealed class SkillCatalog(GameDataStore store) : ISkillCatalog
{
    public bool IsItemSkill(uint skillId) => store.IsItemSkill(skillId);

    public bool HasLevelRow(uint skillId) => store.SkillLevels.ContainsKey(skillId);

    public bool HasTalentLimit(uint skillGroup) => TalentRules.TryGetSkillLevelLimit(store, skillGroup, out _);
}
