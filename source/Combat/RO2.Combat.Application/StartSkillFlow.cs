namespace RO2.Combats.Application;

/// <summary>
/// The 1508 ReqSkillStart cast-accept decision, past the <see cref="CastGate"/> preconditions: whether a cast is
/// accepted, refused on cooldown, or dropped unanswered as a burst duplicate. Pure — the host reads the cooldown,
/// the burst-duplicate flag and whether the group's last cast has landed, and applies the ordered sends, SP spend
/// and cooldown arm itself.
/// </summary>
public static class StartSkillFlow
{
    /// <summary>What a 1508 at the cooldown check resolves to.</summary>
    public enum Outcome
    {
        /// <summary>Not on cooldown: the cast is accepted (the host spends SP, arms the cooldown and broadcasts it).</summary>
        Accept,

        /// <summary>On cooldown: refuse with a 2715 (the host's chosen code) followed by the 2962 broadcast.</summary>
        CooldownRefusal,

        /// <summary>
        /// On cooldown AND a burst duplicate whose first frame's cast has NOT yet landed: answered with nothing —
        /// no 2715, no 2962, no 2716 — and never a second accept.
        /// </summary>
        BurstDropNoAnswer,
    }

    /// <summary>
    /// Decides a 1508 at the cooldown check. Not on cooldown → <see cref="Outcome.Accept"/>. On cooldown: a burst
    /// duplicate whose cast has not landed → <see cref="Outcome.BurstDropNoAnswer"/>; otherwise
    /// <see cref="Outcome.CooldownRefusal"/>. <paramref name="hasLanded"/> is consulted only when on cooldown, so the
    /// host may pass any value (e.g. true) when the skill is not cooling.
    /// </summary>
    public static Outcome Decide(bool onCooldown, bool isBurstDuplicate, bool hasLanded)
    {
        if (!onCooldown)
            return Outcome.Accept;
        if (isBurstDuplicate && !hasLanded)
            return Outcome.BurstDropNoAnswer;
        return Outcome.CooldownRefusal;
    }
}
