namespace RO2.Combats.Domain;

/// <summary>
/// The melee reach constants shared by the player auto-attack gate and the monster swing gate: one source so the two
/// sides cannot drift apart.
/// </summary>
public static class MeleeReach
{
    /// <summary>Melee reach in world units: the smallest round value that admits every observed melee attack
    /// (max 3.73). Also the reach of a monster whose NPCInfo row carries no Attack_Range.</summary>
    public const float MaxAttackRange = 4f;

    /// <summary>Extra reach added on top of the attacker's range, mirroring the client's own range gate
    /// (<c>distance &lt;= range + targetRadius + ~3.9</c>). 3.890625 (= 249/64, exact in a float) is the client's constant.</summary>
    public const float AttackRangeTolerance = 3.890625f;
}
