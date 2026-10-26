using RO2.GameData;
using RO2.Worlds.Domain;

namespace RO2.Worlds.Application;

/// <summary>
/// The decisions of the function-object control flows: begin (1410), cancel (1411) and reward (1412). Each is pure over the
/// player's <see cref="FuncObjectControlSlot"/>: the host performs every send and mutation in its own original order.
/// </summary>
public static class FuncObjectControlUseCase
{
    /// <summary>
    /// Fraction of an object's Casting that must have run since its control began before a 1412 ends it. Half, not the whole:
    /// the client's bar clock can run ahead of wall time, so a genuine 1412 may arrive slightly early.
    /// </summary>
    public const double MinCastingFraction = 0.5;

    /// <summary>Sets <paramref name="control"/> as the player's running control, replacing any previous one.</summary>
    public static void Begin<TPlayer>(IFuncObjectControlSlots<TPlayer> slots, TPlayer player, FuncObjectControl control, long now)
        where TPlayer : class
        => slots.Slot(player).Begin(control, now);

    /// <summary>
    /// Ends the player's running control when it is on <paramref name="target"/> and returns it; false, with the control
    /// left running, when none on that target is.
    /// </summary>
    public static bool TryEnd<TPlayer>(IFuncObjectControlSlots<TPlayer> slots, TPlayer player, uint target, out FuncObjectControl control)
        where TPlayer : class
    {
        control = default;
        return slots.Find(player) is { } slot && slot.TryEnd(target, out control);
    }

    /// <summary>
    /// Ends the player's running control when it is on <paramref name="target"/> and has run at least
    /// <paramref name="minElapsed"/>; see <see cref="FuncObjectControlSlot.TryEndAfter"/>.
    /// </summary>
    public static FuncObjectControlEnd TryEndAfter<TPlayer>(IFuncObjectControlSlots<TPlayer> slots, TPlayer player, uint target,
        TimeSpan minElapsed, long now, out FuncObjectControl control, out TimeSpan elapsed)
        where TPlayer : class
    {
        control = default;
        elapsed = TimeSpan.Zero;
        return slots.Find(player) is { } slot
            ? slot.TryEndAfter(target, minElapsed, now, out control, out elapsed)
            : FuncObjectControlEnd.NotRunning;
    }

    /// <summary>
    /// Whether a cancel that ended a control resets the canceller's state: only the first cancel of a control that began
    /// with a state broadcast does; a repeated cancel, or a control that sent no 2398, does not.
    /// </summary>
    public static bool ResetsStateOnCancel(bool ended, FuncObjectControl control) => ended && control.State is not null;

    /// <summary>
    /// Whether a 1412 on a pose-kind object is answered: only as the end of a control that began with a state broadcast and
    /// has run long enough. Anything else gets nothing.
    /// </summary>
    public static bool AnswersReward(FuncObjectControlEnd ended, FuncObjectControl control)
        => ended == FuncObjectControlEnd.Ended && control.State is not null;

    /// <summary>The time a control must have run before a 1412 may end it: <see cref="MinCastingFraction"/> of the object's Casting.</summary>
    public static TimeSpan RewardMinElapsed(ushort castingMs) => TimeSpan.FromMilliseconds(castingMs * MinCastingFraction);

    /// <summary>
    /// The Position_Info row a portal sends the player to, or null when the row is missing or its map is not in Map_List
    /// (the portal is then not taken).
    /// </summary>
    public static PositionData? PortalDestination(IWorldCatalog catalog, uint positionId)
        => catalog.GetPosition(positionId) is { } pos && catalog.GetMap(pos.Area_ID) is not null ? pos : null;
}
