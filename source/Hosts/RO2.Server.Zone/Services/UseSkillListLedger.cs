using System.Runtime.CompilerServices;
using RO2.GameData.Tables;
using RO2.Guilds.Domain;
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

 /// <summary>The 2488 lists to send after a guild skill list answer (see <see cref="GuildSkillRules.PlanGuildLinkBurst"/>).</summary>
    internal static IReadOnlyList<IReadOnlyList<(uint Group, byte Level)>> PlanGuildLinkBurst(
        IEnumerable<KeyValuePair<uint, byte>> ownSkills, IReadOnlyDictionary<uint, byte>? held,
        IEnumerable<uint> guildSkills, IReadOnlyDictionary<uint, GuildSkillLevel> table)
        => GuildSkillRules.PlanGuildLinkBurst(ownSkills, held, guildSkills, table);
}
