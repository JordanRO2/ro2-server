namespace RO2.Worlds.Domain;

/// <summary>
/// The pure predicates behind a ViewGroupPCMoveTo (1526) report: whether the PC takes moves at all, whether the report
/// is at a dead PC's pin, whether it displaces the avatar, and whether it has arrived. They hold no state and read no
/// clock; the host feeds them the already parsed coordinates and the live flags.
/// </summary>
public static class MoveRequestRules
{
    /// <summary>Displacement (x/y, per axis) below which a 1526 is not a move; the same 0.1 the arrival test uses.</summary>
    public const float ChannelMoveEpsilon = 0.1f;

    /// <summary>
    /// False while the PC has not acked its current map (1304) or is inside a glider re-entry: a 1526 then carries the
    /// previous map's coordinates.
    /// </summary>
    public static bool AcceptsMoves(bool mapLoaded, bool pendingGliderDismount) => mapLoaded && !pendingGliderDismount;

    /// <summary>
    /// True when a dead PC's 1526 reports the point it is pinned at: its current x/y bit-equal to the pinned x/y. Bit
    /// equality: the at-pin 1526s carry the 1679's floats verbatim, and the off-pin ones differ from them.
    /// </summary>
    public static bool IsAtDeadPoint(float curX, float curY, float pinX, float pinY)
        => BitConverter.SingleToInt32Bits(curX) == BitConverter.SingleToInt32Bits(pinX)
           && BitConverter.SingleToInt32Bits(curY) == BitConverter.SingleToInt32Bits(pinY);

    /// <summary>
    /// True when a 1526 displaces the PC: its destination differs from its current point, or its current point from the
    /// last one the server held. A 1526 that only turns the avatar (destination == current == last point) is not a move.
    /// </summary>
    public static bool DisplacesChannel(float lastX, float lastY, float curX, float curY, float destX, float destY)
        => MathF.Abs(destX - curX) >= ChannelMoveEpsilon || MathF.Abs(destY - curY) >= ChannelMoveEpsilon
           || MathF.Abs(curX - lastX) >= ChannelMoveEpsilon || MathF.Abs(curY - lastY) >= ChannelMoveEpsilon;

    /// <summary>True when the destination coincides with the current point on both axes: the client has arrived and stands still.</summary>
    public static bool HasArrived(float curX, float curY, float destX, float destY)
        => MathF.Abs(destX - curX) < ChannelMoveEpsilon && MathF.Abs(destY - curY) < ChannelMoveEpsilon;
}
