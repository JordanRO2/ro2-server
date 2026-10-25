using System.Runtime.CompilerServices;
using RO2.GameData.Tables;
using RO2.Network.ProudNet;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Tracks the skill list each session's client last received in a 2488 OnUseSkillList, and computes the guild link skill
/// bursts sent after a guild skill list request. A 2488 replaces the client's whole list.
/// </summary>
/// <remarks>
/// A 2488 follows a 2723 only when the client does not yet hold the guild's link skills. The burst is one cumulative 2488
/// per missing link, in ascending line order. The record is runtime state and starts empty for each session.
/// </remarks>
internal static class UseSkillListLedger
{
    private sealed class Held
    {
        public IReadOnlyDictionary<uint, byte>? Skills;
    }

    private static readonly ConditionalWeakTable<ProudSession, Held> BySession = new();

 /// <summary>
 /// Records the skill list the session's client now holds. A group listed twice keeps its higher level.
 /// </summary>
    internal static void Record(ProudSession session, IReadOnlyList<(uint Group, byte Level)> skills)
    {
        var map = new Dictionary<uint, byte>(skills.Count);
        foreach (var (group, level) in skills)
            if (level > map.GetValueOrDefault(group)) map[group] = level;
        var held = BySession.GetOrCreateValue(session);
        Volatile.Write(ref held.Skills, map);
    }

 /// <summary>The list last recorded for the session (group to level), or null.</summary>
    internal static IReadOnlyDictionary<uint, byte>? HeldBy(ProudSession session)
        => BySession.TryGetValue(session, out var held) ? Volatile.Read(ref held.Skills) : null;

 /// <summary>
 /// The 2488 lists to send after a guild skill list answer. Empty when the client already holds every link skill.
 /// Otherwise one cumulative list per missing link, in line order; the last one is the full guild list.
 /// </summary>
 /// <param name="ownSkills">The character's own skills, group to level.</param>
 /// <param name="guildSkills">The guild's learned skill ids (line * 100 + level).</param>
 /// <param name="table">Guild_Skill_Level by Guild_Skill_ID.</param>
    internal static IReadOnlyList<IReadOnlyList<(uint Group, byte Level)>> PlanGuildLinkBurst(
        IEnumerable<KeyValuePair<uint, byte>> ownSkills, IReadOnlyDictionary<uint, byte>? held,
        IEnumerable<uint> guildSkills, IReadOnlyDictionary<uint, GuildSkillLevel> table)
    {
        if (held is null) return [];
        var own = new Dictionary<uint, byte>();
        foreach (var (group, level) in ownSkills)
            if (level > own.GetValueOrDefault(group)) own[group] = level;

        
        
        var links = new List<(uint Group, byte Level)>();
        foreach (var (line, level) in GuildSkillLearnPackets.LineLevels(guildSkills).OrderBy(kv => kv.Key))
        {
            if (!table.TryGetValue(line * 100 + (uint)level, out var row) || row.Link_Skill_ID == 0) continue;
            uint group = row.Link_Skill_ID / 100;
            byte need = Math.Max((byte)(row.Link_Skill_ID % 100), own.GetValueOrDefault(group));
            links.Add((group, need));
        }

        var missing = links.Where(l => held.GetValueOrDefault(l.Group) != l.Level).ToList();
        if (missing.Count == 0) return [];

        var current = new Dictionary<uint, byte>(own);
        foreach (var l in links)
            if (!missing.Contains(l)) current[l.Group] = l.Level;

        var burst = new List<IReadOnlyList<(uint Group, byte Level)>>(missing.Count);
        foreach (var (group, level) in missing)
        {
            current[group] = level;
            burst.Add(current.Select(kv => (kv.Key, kv.Value)).ToArray());
        }
        return burst;
    }
}
