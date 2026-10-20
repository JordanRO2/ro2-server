using System.Runtime.CompilerServices;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Tracks who a player is attacking. It does NOT drive swings: the server never clocks a player's basic attack.
/// </summary>
/// <remarks>
/// <para>
/// A basic attack is a client-clocked request/response cycle:
/// <code>
///   C2S 1498 ReqNormalAttackStart
///      -> S2C 2694 NfyNormalAttackStart   the server's GRANT
///      -> C2S 1499 ReqDamageNormalAttack  the client's animation reaches impact
///      -> S2C 2696 NfyAttackDamage        the server's damage ANSWER
///      -> the next 1498
/// </code>
/// The grant gates the rest: a request that received a 2694 is normally followed by a 1499.
/// </para>
/// <para>
/// The server must not be the clock. A player's swing waits on the client's animation, while a monster's swing is
/// answered immediately; a server-side timer would make every player behave like a monster, with no coherent
/// attack animation, the client not showing the attack as active, and multiple hits landing at once.
/// </para>
/// <para>
/// So this type only remembers the engagement, which the 1499 handler uses to validate that damage
/// belongs to a target the player actually engaged, and which monster AI also needs.
/// </para>
/// </remarks>
public static class AutoAttackState
{
    /// <summary>Mask of an actor handle's low byte, its actor type tag.</summary>
    private const uint ActorTagMask = 0xFF;

    /// <summary>Melee reach in world units.</summary>
    /// <remarks>
    /// <para>
    /// C2S 1498 carries the client's own position as three floats at body offsets +12/+16/+20 alongside the target
    /// handle, so the distance a client attacks from can be computed directly. Melee attacks land from at most
    /// about 3.7 units away:
    /// </para>
    /// <code>
    ///   min 0.27   median 1.25   p90 2.13   p99 3.42   max 3.73
    /// </code>
    /// <para>
    /// 4 is the smallest round value that admits every such attack; 3 would refuse some legitimate ones.
    /// </para>
    /// </remarks>
    public const float MaxAttackRange = 4f;

    /// <summary>
    /// Records that the client asked to attack a target, and was granted it. This also ARMS the swing: the next
    /// 1499 impact is this 1498's (see <see cref="TryConsumeSwing(PlayerState)"/>), and the swing is BOUND to
    /// <paramref name="targetEntityId"/> and to the arming time (<see cref="PlayerState.LastAttackRequestAt"/>), so
    /// an impact can neither name another target nor land sooner than the interval floor after it.
    /// </summary>
    public static void Engage(PlayerState player, uint targetEntityId)
    {
        player.TargetEntityId = targetEntityId;
        player.IsInCombat = true;
        var swing = Swings.GetValue(player, _ => new SwingFlag());
        lock (swing)
        {
            player.LastAttackRequestAt = DateTime.UtcNow;
            swing.Target = targetEntityId;
            swing.Pending = true;
        }
    }

    /// <summary>
    /// Takes the swing the latest granted 1498 armed: true for the FIRST 1499 impact after it, false for any
    /// later one (and for a player who was never granted a swing).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only one impact per swing is damaged; the others are refused with 2695 code 20158. The client
    /// double-sends impacts (a few milliseconds apart) and re-reports after the 2696, and what separates the
    /// refusal is whether the 1499 is the first since the latest 1498. A first 1499 after a 1498 gets a 2696,
    /// while a repeat 1499 (no 1498 since the previous 1499) gets a 20158. The refusal is neither range nor
    /// death (20158 goes to live and dead targets alike), and a skill cast between the grant and the impact does
    /// not break the pairing.
    /// </para>
    /// <para>
    /// So the flag is only ever cleared by a 1499, never by <see cref="Disengage"/>: a kill or a refusal of
    /// something else in between must not turn the pending impact into a refusal. Kept per PlayerState in a
    /// <see cref="ConditionalWeakTable{TKey,TValue}"/> (the SkillTickTargets pattern): it leaves with the player
    /// and needs no DI registration.
    /// </para>
    /// </remarks>
    public static bool TryConsumeSwing(PlayerState player)
        => TryConsumeSwing(player, DateTime.UtcNow, TimeSpan.Zero, out _);

    /// <summary>
    /// <see cref="TryConsumeSwing(PlayerState)"/> that also enforces the interval floor and returns the target the
    /// swing is bound to: true only for the first impact after a granted 1498 that arrives at least
    /// <paramref name="minInterval"/> after that 1498 (<see cref="PlayerState.LastAttackRequestAt"/>, set by
    /// <see cref="Engage"/>). The caller must still require the impact's own target to equal
    /// <paramref name="boundTarget"/>, or a 1499 could be spent on a target other than the one the 1498 named.
    /// </summary>
    /// <param name="now">The impact's arrival time.</param>
    /// <param name="minInterval">Smallest 1498-to-impact gap accepted; <see cref="TimeSpan.Zero"/> disables the floor.</param>
    /// <param name="boundTarget">The target the pending swing is bound to; 0 when no swing is pending.</param>
    /// <remarks>
    /// A swing refused for arriving too early is NOT consumed: the impact the client sends once its animation
    /// reaches the floor still lands, and re-sending 1498 re-arms (restarts) the clock, so the best a modified
    /// client reaches is one impact per floor interval. UNVERIFIED: the floor for a given weapon.
    /// <see cref="DefaultMinSwingInterval"/> is a single weapon-blind figure; a per-weapon attack speed would be more exact.
    /// </remarks>
    public static bool TryConsumeSwing(PlayerState player, DateTime now, TimeSpan minInterval, out uint boundTarget)
    {
        boundTarget = 0;
        if (!Swings.TryGetValue(player, out var swing))
            return false;
        lock (swing)
        {
            if (!swing.Pending)
                return false;
            boundTarget = swing.Target;
            // Rate floor measured between consecutive ACCEPTED swings, NOT arm-to-impact: the first impact after an
            // Engage (its LastAttackRequestAt is "now") must always land, so compare to the last accepted impact.
            // Anti-flood only; DefaultMinSwingInterval is a proxy.
            if (minInterval > TimeSpan.Zero && swing.LastImpact != default && now - swing.LastImpact < minInterval)
                return false;
            swing.Pending = false;
            swing.LastImpact = now;
            return true;
        }
    }

    /// <summary>
    /// The 1498-to-impact floor (200 ms). It is a weapon-blind proxy: a PC attacker's grant-to-answer gap
    /// (2694 to 2696) is typically several hundred milliseconds, well above this floor, which only has to stop
    /// impact flooding. A per-weapon floor from attack speed would be more exact.
    /// </summary>
    public static readonly TimeSpan DefaultMinSwingInterval = TimeSpan.FromMilliseconds(200);

    private sealed class SwingFlag
    {
        public bool Pending;
        public uint Target;
        public DateTime LastImpact;
    }

    private static readonly ConditionalWeakTable<PlayerState, SwingFlag> Swings = new();

    /// <summary>
    /// Whether <paramref name="handle"/>'s low byte is an actor type tag an attack can name: 0x11 PC
    /// (<see cref="PlayerStateManager.PcActorTag"/>), 0x13 NPC (<see cref="NpcStateManager.NpcActorTag"/>) or
    /// 0x14 function object (<see cref="FuncObjectRegistry.FuncObjectActorTag"/>). A 1498 on anything else is refused with 2695 code 20014 (NormalAttackStartHandler).
    /// </summary>
    public static bool IsActorHandle(uint handle)
        => (handle & ActorTagMask) is PlayerStateManager.PcActorTag or NpcStateManager.NpcActorTag
            or FuncObjectRegistry.FuncObjectActorTag;

    /// <summary>Clears the engagement.</summary>
    public static void Disengage(PlayerState player)
    {
        player.IsInCombat = false;
        player.TargetEntityId = 0;
    }

    /// <summary>
    /// Extra reach added on top of the weapon's Auto_Attack_Range, mirroring the client's own range gate
    /// (<c>distance ≤ range + targetRadius + ~3.9</c>). It also keeps the ≤4-unit melee reach intact: a melee
    /// weapon carries range 2, so 2 + 3.89 = 5.89 comfortably admits every melee impact (≤3.73).
    /// 3.890625 (= 249/64, exact in a float) is the client's constant. Not modelled: the client also adds the
    /// target's size term for large targets, so for a large target this reach is tighter than the client's.
    /// </summary>
    public const float AttackRangeTolerance = 3.890625f;

    /// <summary>
    /// Whether the player is close enough to the target position to attack, using the EQUIPPED WEAPON's
    /// range. A bow carries Auto_Attack_Range 20, a melee weapon 2; unarmed (0) falls back to the melee
    /// <see cref="MaxAttackRange"/>. Without this a bow user's swing was gated on melee reach (4), so the
    /// self-2694 grant that drives 1498→1499 never fired at range and the bow attack never completed.
    /// </summary>
    public static bool InRange(PlayerState player, float x, float y)
    {
        var dx = x - player.X;
        var dy = y - player.Y;
        float reach = (player.AutoAttackRange > 0 ? player.AutoAttackRange : MaxAttackRange) + AttackRangeTolerance;
        return MathF.Sqrt(dx * dx + dy * dy) <= reach;
    }
}
