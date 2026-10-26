using System.Diagnostics;
using System.Runtime.CompilerServices;
using RO2.Server.Zone.Models;
using RO2.Worlds.Application;
using RO2.Worlds.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Tracks the one running function-object control per player. The slot (<see cref="FuncObjectControlSlot"/>) is stored beside
/// the <see cref="PlayerState"/> in a ConditionalWeakTable, so it is released with the player; the begin and end decisions are
/// <see cref="FuncObjectControlUseCase"/>.
/// </summary>
/// <remarks>
/// A control starts on a 1410 request and ends on the matching 1411 or 1412 for the same target. A 1411 or 1412
/// for another target leaves the running control alone.
/// </remarks>
public static class FuncObjectControlState
{
    private sealed class Slots : IFuncObjectControlSlots<PlayerState>
    {
        private readonly ConditionalWeakTable<PlayerState, FuncObjectControlSlot> _controls = new();

        public FuncObjectControlSlot Slot(PlayerState player) => _controls.GetValue(player, static _ => new FuncObjectControlSlot());

        public FuncObjectControlSlot? Find(PlayerState player) => _controls.TryGetValue(player, out var slot) ? slot : null;
    }

    private static readonly Slots Controls = new();

    /// <summary>
    /// Sets <paramref name="control"/> as the player's running control, replacing any previous one, and records
    /// when it began.
    /// </summary>
    public static void Begin(PlayerState player, FuncObjectControl control)
    {
        FuncObjectControlUseCase.Begin(Controls, player, control, Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// Ends the running control when it is on <paramref name="target"/> and began at least
    /// <paramref name="minElapsed"/> before <paramref name="now"/>, and returns it. A control on that target that
    /// began more recently keeps running and yields <see cref="FuncObjectControlEnd.TooEarly"/>.
    /// </summary>
    public static FuncObjectControlEnd TryEndAfter(PlayerState player, uint target, TimeSpan minElapsed, long now,
        out FuncObjectControl control, out TimeSpan elapsed)
        => FuncObjectControlUseCase.TryEndAfter(Controls, player, target, minElapsed, now, out control, out elapsed);

    /// <summary>
    /// Ends the running control when it is on <paramref name="target"/> and returns it. Returns false, and leaves
    /// any running control in place, when no control on that target is running.
    /// </summary>
    public static bool TryEnd(PlayerState player, uint target, out FuncObjectControl control)
        => FuncObjectControlUseCase.TryEnd(Controls, player, target, out control);

    /// <summary>The player's running control, or null (for tests and diagnostics; the handlers go through TryEnd).</summary>
    internal static FuncObjectControl? Current(PlayerState player) => Controls.Find(player)?.Current;
}
