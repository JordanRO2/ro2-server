using System.Runtime.CompilerServices;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Per-player Illusion Pyramid run state: the open run, whether the pyramid was used since the last departure,
/// and the time of the last pyramid request. It lives on the player state, so a new character or a relog starts
/// with no run.
/// </summary>
/// <remarks>
/// <para>
/// A run still open when the player leaves is settled at departure, and the window is closed with an
/// unsolicited close message. A run left open when the connection drops is not settled.
/// </para>
/// <para>
/// All access except <see cref="Slot.LastRequestMs"/> goes through <see cref="Slot.Gate"/>, an async lock.
/// </para>
/// </remarks>
public static class IllusionPyramidRunStore
{
 /// <summary>A run in progress.</summary>
    public sealed class Run
    {
 /// <summary>How the run was entered. This determines its cost when it settles.</summary>
        public IllusionPyramidCatalog.EntryKind Entry { get; set; }

 /// <summary>The floor of the last accepted draw.</summary>
        public byte Floor { get; set; }

 /// <summary>The result of the last draw, held until it is settled, or null after a settlement.</summary>
        public IllusionPyramidCatalog.Reward? Held { get; set; }

 /// <summary>False from an accepted draw until its result is fetched; further draws are refused meanwhile.</summary>
        public bool Fetched { get; set; }

 /// <summary>True when the fetched result ends the run; further draws are refused until it is settled.</summary>
        public bool Ended { get; set; }
    }

 /// <summary>The per-player state.</summary>
    public sealed class Slot
    {
        public readonly SemaphoreSlim Gate = new(1, 1);

 /// <summary>The open run, or null when none is open.</summary>
        public Run? Run { get; set; }

 /// <summary>Tick count of the last accepted draw, used by the speed check; cleared on settlement.</summary>
        public long? LastAcceptedMs { get; set; }

 /// <summary>True when a draw was accepted since the last settlement.</summary>
        public bool DrewSinceFinish { get; set; }

 /// <summary>
 /// True when a pyramid request came since the last departure close. Cleared only by the close sent before a
 /// departure that is not an in-place map change.
 /// </summary>
        public bool UsedSinceLeave { get; set; }

        private long _lastRequestMs;

 /// <summary>
 /// Tick count of the last pyramid request (0 before the first). Read and written without the gate, so the trade
 /// handler never waits on a pyramid request.
 /// </summary>
        public long LastRequestMs
        {
            get => Volatile.Read(ref _lastRequestMs);
            set => Volatile.Write(ref _lastRequestMs, value);
        }
    }

 /// <summary>How long after a pyramid request a trade naming that player is refused.</summary>
    public const long TradeRefusalWindowMs = 3000;

 /// <summary>
 /// Returns true when <paramref name="player"/> sent a pyramid request less than <paramref name="windowMs"/>
 /// ago. Reads without creating a slot and without taking the gate.
 /// </summary>
 /// <remarks>
 /// Used to refuse a trade request with result 10 while the target is in the pyramid.
 /// </remarks>
    public static bool RecentlyUsed(PlayerState player, long nowMs, long windowMs = TradeRefusalWindowMs)
    {
        if (!s_slots.TryGetValue(player, out var slot)) return false;
        var last = slot.LastRequestMs;
        return last != 0 && nowMs - last < windowMs;
    }

    private static readonly ConditionalWeakTable<PlayerState, Slot> s_slots = new();

 /// <summary>Returns the player's slot, created on first use.</summary>
    public static Slot For(PlayerState player) => s_slots.GetValue(player, _ => new Slot());
}
