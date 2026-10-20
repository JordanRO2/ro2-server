using System.Runtime.CompilerServices;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// The actors each skill of a PC last hit, as its 2718 NfySkillDamage carried them: written by the 1509
/// ReqSkillDamage answer (SkillDamageHandler) and read by the 1549 ReqSkillDamageTick answer
/// (SkillDamageTickHandler), whose 2797 NfySkillDamageTick hits that same set.
/// </summary>
/// <remarks>WHY THE TICK NEEDS IT. ORDER.</remarks>
public static class SkillTickTargets
{
 /// <summary>
 /// The most actors one RMI 2797 tick can carry; see <see
 /// cref="SkillDamagePackets.MaxSkillDamageTickTargets"/>.
 /// </summary>
    public const int MaxTargets = SkillDamagePackets.MaxSkillDamageTickTargets;

 /// <summary>The actors one cast's 2718 carried, and the map it was cast on.</summary>
    public readonly record struct CastTargets(uint MapId, uint[] Handles);

    private sealed class Casts
    {
        public readonly Dictionary<uint, CastTargets> BySkill = new();
    }

    private static readonly ConditionalWeakTable<PlayerState, Casts> Store = new();

 /// <summary>
 /// Records the actors <paramref name="caster"/>'s 2718 for <paramref name="skillId"/> carried,
 /// replacing the previous cast of that exact skill id (a level is its own id, as the 1549 and 2797
 /// carry it). Zero handles and repeats are left out; the order is kept.
 /// </summary>
    public static void Record(PlayerState caster, uint mapId, uint skillId, IEnumerable<uint> handles)
    {
        var list = new List<uint>();
        foreach (var h in handles)
        {
            if (h != 0 && !list.Contains(h))
                list.Add(h);
        }

        var casts = Store.GetValue(caster, _ => new Casts());
        lock (casts)
            casts.BySkill[skillId] = new CastTargets(mapId, list.ToArray());
    }

 /// <summary>Empty when there is no record or the cast was on another map.</summary>
    public static IReadOnlyList<uint> Recorded(PlayerState caster, uint mapId, uint skillId)
    {
        if (!Store.TryGetValue(caster, out var casts))
            return Array.Empty<uint>();
        lock (casts)
        {
            return casts.BySkill.TryGetValue(skillId, out var cast) && cast.MapId == mapId
                ? cast.Handles
                : Array.Empty<uint>();
        }
    }

 /// <summary>
 /// The recorded actors a tick can still hit, in record order, at most <see cref="MaxTargets"/>:
 /// monsters (Camp 2+) that <paramref name="lookup"/> (NpcStateManager.GetNpc) still knows, not
 /// dead, and on <paramref name="mapId"/>.
 /// </summary>
    public static List<(uint Handle, NpcRuntimeState Npc)> LiveTargets(IReadOnlyList<uint> recorded, uint mapId,
        Func<uint, NpcRuntimeState?> lookup)
    {
        var live = new List<(uint, NpcRuntimeState)>(Math.Min(recorded.Count, MaxTargets));
        foreach (var handle in recorded)
        {
            if (live.Count == MaxTargets)
                break;
            if (lookup(handle) is not { } npc || npc.IsDead || npc.Camp <= 1 || npc.MapId != mapId)
                continue;
            live.Add((handle, npc));
        }
        return live;
    }
}
