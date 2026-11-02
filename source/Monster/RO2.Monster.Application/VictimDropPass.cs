namespace RO2.Monster.Application;

/// <summary>What removing the victim from one monster's threat table did: whether it held a row, and the monster's target after.</summary>
public readonly record struct VictimRemoval(bool Held, uint Target);

/// <summary>The live-state step and the wire effect the pass orders per monster; the host applies both.</summary>
public interface IVictimThreatPorts
{
    /// <summary>Under the monster's threat lock: drops the victim's row and, when the victim was the target, re-selects the top row.</summary>
    VictimRemoval RemoveVictim(uint monsterOid, uint victimHandle);

    /// <summary>The victim left the monster's table: the 2630 removal entry and the target-change push.</summary>
    Task NotifyVictimRemovedAsync(uint monsterOid, VictimRemoval removal);
}

/// <summary>
/// A player who left the fight (died) is removed from every hostile monster's threat table; the monsters that held it, plus
/// the killer, that no live player is still engaged with are released. The state change and the wire effect of each monster
/// run in turn through the ports, as the host always applied them; the PC-death sends stay in the host.
/// </summary>
public static class VictimDropPass
{
    /// <summary>A monster leaves combat when it held the victim or is the killer, and no live player is engaged with it.</summary>
    public static bool IsReleased(bool held, bool isKiller, bool engaged) => (held || isKiller) && !engaged;

    /// <summary>
    /// Runs the removal over <paramref name="hostileMonsters"/> in order and returns the released monsters, in the same order.
    /// <paramref name="engagedTargets"/> is the set of monster handles a live player in combat is targeting.
    /// </summary>
    public static async Task<List<uint>> RunAsync(IVictimThreatPorts ports, uint victimHandle, uint killerHandle,
        IEnumerable<uint> hostileMonsters, IReadOnlySet<uint> engagedTargets)
    {
        var released = new List<uint>();
        foreach (var oid in hostileMonsters)
        {
            var removal = ports.RemoveVictim(oid, victimHandle);
            if (removal.Held)
                await ports.NotifyVictimRemovedAsync(oid, removal);
            if (IsReleased(removal.Held, oid == killerHandle, engagedTargets.Contains(oid)))
                released.Add(oid);
        }
        return released;
    }
}
