using RO2.Domain.Entities;

namespace RO2.Application.Interfaces;

public interface ISkillRepository
{
    Task<IReadOnlyList<CharacterSkill>> GetSkillsAsync(uint characterId);

    /// <summary>
    /// Learns the skill at <paramref name="level"/>, or raises it to that level; a lower level never downgrades.
    /// Idempotent and upsert-safe, so two writers racing for the same (character, skill) end with one row.
    /// </summary>
    Task LearnSkillAsync(uint characterId, uint skillId, byte level);
    Task UpdateSkillLevelAsync(uint characterId, uint skillId, byte newLevel);

    /// <summary>
    /// Sets every <paramref name="skills"/> entry to its exact level (inserting the row when it is not learned yet) in
    /// ONE batch: all of it is written or, when the call throws, the caller must treat none of it as written.
    /// A skill id listed twice takes its last level. An empty list writes nothing.
    /// </summary>
    Task UpsertSkillsAsync(uint characterId, IReadOnlyList<(uint SkillId, byte Level)> skills, CancellationToken ct = default);

    /// <summary>Delete the learned rows of these skill ids for the character.</summary>
    Task DeleteSkillsAsync(uint characterId, IReadOnlyCollection<uint> skillIds, CancellationToken ct = default);

    /// <summary>Creates the unique (character, skill) index; false when existing rows already collide. No-op by default.</summary>
    Task<bool> EnsureIndexesAsync() => Task.FromResult(true);
}
