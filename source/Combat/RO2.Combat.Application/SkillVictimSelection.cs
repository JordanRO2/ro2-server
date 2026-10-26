namespace RO2.Combats.Application;

/// <summary>
/// Which of the monsters a 1509 names an attack skill actually damages. The caps and actor-handle conventions are
/// the host's (the wire target cap, the PC-handle tag, the friendly-camp threshold), passed in as
/// <see cref="VictimSelectionRules"/> so the rule itself is pure and testable.
/// </summary>
public static class SkillVictimSelection
{
    /// <summary>
    /// The host-owned caps and conventions the selection reads: <paramref name="MaxHits"/> is the wire target cap
    /// (SkillDamagePackets.MaxSkillDamageTargets); a handle is a PC when <c>(handle &amp; ActorTagMask) == PcActorTag</c>;
    /// a camp of <paramref name="FriendlyCampMax"/> or below is a non-monster and takes no damage.
    /// </summary>
    public readonly record struct VictimSelectionRules(int MaxHits, byte PcActorTag, uint ActorTagMask, int FriendlyCampMax);

    /// <summary>
    /// The monsters an attack skill damages, from the set the 1509 named: the primary alone when it is an NPC, none
    /// when the primary is another PC (not the caster), else the live hit-list monsters up to the smaller of
    /// Skill_Damage_Quantity (<paramref name="damageQuantity"/>, null = no skill limit) and the wire cap. A victim
    /// must be a live NPC whose record still carries that exact handle (EntityOid) and whose camp is above the
    /// friendly threshold; duplicates and zero handles are left out and the order is kept.
    /// </summary>
    public static List<uint> SelectNpcVictims(byte hasTarget, uint primaryTarget, IReadOnlyList<uint> targets,
        uint casterHandle, ICombatantView view, int? damageQuantity, VictimSelectionRules rules)
    {
        var victims = new List<uint>();
        if (hasTarget != 0 && primaryTarget != 0)
        {
            if (view.Find(primaryTarget) is not null)
            {
                victims.Add(primaryTarget);
                return victims;
            }
            if ((primaryTarget & rules.ActorTagMask) == rules.PcActorTag && primaryTarget != casterHandle)
                return victims;
        }

        var cap = Math.Min(rules.MaxHits, damageQuantity ?? rules.MaxHits);
        foreach (var handle in targets)
        {
            if (victims.Count >= cap) break;
            if (view.Find(handle) is not { } npc || npc.IsDead || npc.EntityOid != handle || npc.Camp <= rules.FriendlyCampMax)
                continue;
            if (!victims.Contains(handle)) victims.Add(handle);
        }
        return victims;
    }
}
