namespace RO2.Combats.Domain;

/// <summary>
/// Whether a 1498 ReqNormalAttackStart may be granted against a resolved NPC target: the target must be a known,
/// live, hostile monster. Range is NOT a precondition of the grant (1498 is sent before the client runs to the
/// target; reach is enforced at the 1499 impact). The caller handles the non-actor handle and the field-duel PC
/// target before this, and maps the refusal to its 2695 wire code.
/// </summary>
public static class SwingVerdict
{
    /// <summary>Camp of this value or below is a friendly / non-monster NPC a normal attack cannot swing at.</summary>
    private const int FriendlyCampMax = 1;

    /// <summary>The verdict on an NPC target of a 1498 swing request.</summary>
    public enum Verdict
    {
        /// <summary>The swing may be granted.</summary>
        Grant,
        /// <summary>The handle did not resolve to a known NPC.</summary>
        NotKnownActor,
        /// <summary>The target NPC is dead.</summary>
        TargetDead,
        /// <summary>The target NPC is friendly / non-hostile (camp at or below the friendly threshold).</summary>
        NotHostile,
    }

    /// <summary>
    /// The verdict on an NPC target: <see cref="Verdict.NotKnownActor"/> when it did not resolve, else
    /// <see cref="Verdict.TargetDead"/> when dead, else <see cref="Verdict.NotHostile"/> when its camp is friendly,
    /// else <see cref="Verdict.Grant"/>. <paramref name="npcIsDead"/> / <paramref name="npcCamp"/> are read only when
    /// <paramref name="npcResolved"/> is true (the caller may pass defaults otherwise).
    /// </summary>
    public static Verdict ForTarget(bool npcResolved, bool npcIsDead, int npcCamp)
    {
        if (!npcResolved)
            return Verdict.NotKnownActor;
        if (npcIsDead)
            return Verdict.TargetDead;
        if (npcCamp <= FriendlyCampMax)
            return Verdict.NotHostile;
        return Verdict.Grant;
    }
}
