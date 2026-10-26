using RO2.Worlds.Domain;

namespace RO2.Worlds.Application;

/// <summary>
/// The decisions of a ViewGroupPCMoveTo (1526) report, over the pure <see cref="MoveRequestRules"/>. The report is handled
/// in three steps with host side effects between them (the movement guard validates and stores the position; the channel
/// end mutates the PC), so there are three decisions rather than one. Each is pure: the host reads the live state and
/// applies every store, send and mutation in its own original order.
/// </summary>
public static class MoveRequestFlow
{
    /// <summary>What the first gate resolves to.</summary>
    public enum Admission
    {
        /// <summary>Malformed, or the PC does not take moves yet: nothing is stored or relayed.</summary>
        Drop,

        /// <summary>The report goes on to the dead-PC and validation steps.</summary>
        Proceed,
    }

    /// <summary>What a report resolves to while the PC may be dead.</summary>
    public enum DeadOutcome
    {
        /// <summary>The PC is alive: validate the move.</summary>
        Alive,

        /// <summary>The PC is dead and the report is off its pin: dropped, the position is kept.</summary>
        DropOffPin,

        /// <summary>The PC is dead and the report is at its pin: relayed to the onlookers, the position is not taken.</summary>
        RelayAtPin,
    }

    /// <summary>The decision after the guard accepted a move.</summary>
    /// <param name="ChannelToEnd">The channel skill the move ends, or 0. The host ends it (which can still fail) and cancels only if that succeeds.</param>
    /// <param name="IsMoving">The PC's moving flag after this report: false once the client has arrived.</param>
    public readonly record struct AcceptedMove(uint ChannelToEnd, bool IsMoving);

    /// <summary>
    /// First gate: only a canonical-length frame from a PC that acked its map and is not in a glider re-entry is a
    /// movement report.
    /// </summary>
    public static Admission Admit(bool canonicalLength, bool mapLoaded, bool pendingGliderDismount)
        => canonicalLength && MoveRequestRules.AcceptsMoves(mapLoaded, pendingGliderDismount)
            ? Admission.Proceed
            : Admission.Drop;

    /// <summary>
    /// A dead PC's report is relayed only at the point it is pinned at (<paramref name="atDeadPoint"/>, see
    /// <see cref="MoveRequestRules.IsAtDeadPoint"/>); an alive PC's goes on to validation.
    /// </summary>
    public static DeadOutcome DecideDead(bool isDead, bool atDeadPoint)
        => !isDead ? DeadOutcome.Alive : atDeadPoint ? DeadOutcome.RelayAtPin : DeadOutcome.DropOffPin;

    /// <summary>
    /// Decides an accepted move against the previous accepted point (<paramref name="lastX"/>/<paramref name="lastY"/>).
    /// A move ends the active channel only when there is one, the report displaces the avatar and
    /// <paramref name="cancelsOnMove"/> says the channel is immovable; the predicate is consulted only then.
    /// </summary>
    public static AcceptedMove DecideAccepted(
        float lastX, float lastY, float curX, float curY, float destX, float destY,
        uint activeChannelSkillId, Func<uint, bool> cancelsOnMove)
    {
        var channel = activeChannelSkillId;
        if (channel == 0
            || !MoveRequestRules.DisplacesChannel(lastX, lastY, curX, curY, destX, destY)
            || !cancelsOnMove(channel))
            channel = 0;
        return new AcceptedMove(channel, !MoveRequestRules.HasArrived(curX, curY, destX, destY));
    }
}
