using RO2.GameData;
using RO2.Skills.Domain;

namespace RO2.Skills.Application;

/// <summary>
/// Seeds a character's class starter skills at login: mirror the loaded rows onto the live tree, then LEARN each
/// starter the character is missing (self-healing, so a character made before this, or one that just job-changed,
/// gains its basics on relog), and return the loaded rows plus the newly seeded ones for the 2488 send. The host
/// keeps the DB read, the healed-class drop, the player lookup and the downstream world-entry send.
/// </summary>
public static class SeedStarterSkillsFlow
{
    /// <summary>
    /// Overlays <paramref name="learned"/>, seeds the job's missing starters (persisting each at level 1 and
    /// mirroring them), and returns the merged list (loaded rows first, seeded appended). The persist precedes the
    /// mirror. When the class has no starters, or none is missing, the input list is returned unchanged.
    /// </summary>
    /// <param name="session">The live learned-skill tree to mirror onto.</param>
    /// <param name="repo">The skill repository.</param>
    /// <param name="store">Game data, for the starter set.</param>
    /// <param name="job">The character's class.</param>
    /// <param name="characterId">The character the starters persist for.</param>
    /// <param name="learned">The character's loaded skill rows (after the healed-class drop).</param>
    public static async Task<IReadOnlyList<CharacterSkill>> SeedAsync(ILearnedSkillsSession session,
        ISkillRepository repo, GameDataStore store, byte job, uint characterId, IReadOnlyList<CharacterSkill> learned)
    {
        session.Overlay(learned.Select(sk => (sk.SkillId, sk.Level)));

        var starters = TalentRules.StarterSkillGroups(store, job);
        if (starters.Length == 0) return learned;

        var known = new HashSet<uint>(learned.Select(s => s.SkillId));
        var seeded = new List<CharacterSkill>();
        foreach (var group in starters)
        {
            if (!known.Add(group)) continue;
            await repo.LearnSkillAsync(characterId, group, 1);
            seeded.Add(new CharacterSkill { CharacterId = characterId, SkillId = group, Level = 1 });
        }
        if (seeded.Count == 0) return learned;

        session.Overlay(seeded.Select(s => (s.SkillId, s.Level)));
        var merged = new List<CharacterSkill>(learned);
        merged.AddRange(seeded);
        return merged;
    }
}
