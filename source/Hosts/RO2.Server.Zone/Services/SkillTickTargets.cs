using System.Runtime.CompilerServices;
using RO2.Server.Zone.Models;
using RO2.Combats.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Host facade over one <see cref="SkillTickTargetLedger"/> per PlayerState: the actors each skill of a PC last
/// hit, as its 2718 NfySkillDamage carried them (written by the 1509 answer, read by the 1549 answer, whose 2797
/// hits that set). The ledger logic and its lock live in <see cref="SkillTickTargetLedger"/> (Combat.Domain);
/// this maps player → ledger through a <see cref="ConditionalWeakTable{TKey,TValue}"/> (so it leaves with the
/// player and a relog starts empty) and filters the recorded handles against the live world
/// (<see cref="LiveTargets"/>), which the Domain does not know.
/// </summary>
/// <remarks>WHY THE TICK NEEDS IT. ORDER.</remarks>
public static class SkillTickTargets
{
 /// <summary>
 /// The most actors one RMI 2797 tick can carry; see <see
 /// cref="SkillDamagePackets.MaxSkillDamageTickTargets"/>.
 /// </summary>
    public const int MaxTargets = SkillDamagePackets.MaxSkillDamageTickTargets;

    private static readonly ConditionalWeakTable<PlayerState, SkillTickTargetLedger> Store = new();

 /// <summary>
 /// Records the actors <paramref name="caster"/>'s 2718 for <paramref name="skillId"/> carried,
 /// replacing the previous cast of that exact skill id (a level is its own id, as the 1549 and 2797
 /// carry it). Zero handles and repeats are left out; the order is kept.
 /// </summary>
    public static void Record(PlayerState caster, uint mapId, uint skillId, IEnumerable<uint> handles)
        => Store.GetValue(caster, _ => new SkillTickTargetLedger()).Record(mapId, skillId, handles);

 /// <summary>Empty when there is no record or the cast was on another map.</summary>
    public static IReadOnlyList<uint> Recorded(PlayerState caster, uint mapId, uint skillId)
        => Store.TryGetValue(caster, out var ledger) ? ledger.Recorded(mapId, skillId) : Array.Empty<uint>();

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
