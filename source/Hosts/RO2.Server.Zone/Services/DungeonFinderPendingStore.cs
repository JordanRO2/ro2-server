using System.Runtime.CompilerServices;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Per-player dungeon finder state that outlives the queue: the match announced by a 2808 that is awaiting its
/// 1559, and the finder run started by a 1559, which the final boss kill settles with the 2816 reward.
/// </summary>
/// <remarks>
/// <para>
/// The run is kept per character ID, not per connection, because moving into the run is a channel hand-off that
/// creates a new connection and player state. A run ends when its instance is released, replaced, or closed.
/// </para>
/// <para>
/// A boss kill ends the run and starts the cleared lifetime of its instance.
/// </para>
/// </remarks>
public static class DungeonFinderPendingStore
{
 /// <summary>A match awaiting its 1559: the list, the searcher's role, and the group counts for 2824.</summary>
    public sealed record PendingMatch(uint ListId, byte Role, DungeonFinderQueue.RoleCounts Group);

 /// <summary>
 /// A run started by the finder: the list, the map of its arrival point, and the registry instance the join
 /// entered (null when the arrival is not an instance map).
 /// </summary>
    public sealed record FinderRun(uint ListId, uint ArrivalMapId, long? InstanceId);

    private sealed class Slot
    {
        public readonly Lock Gate = new();
        public PendingMatch? Pending;
    }

    private static readonly ConditionalWeakTable<PlayerState, Slot> s_slots = new();

    private static readonly Lock s_runsGate = new();
    private static readonly Dictionary<uint, FinderRun> s_runs = new();

 /// <summary>Records the match a 2808 is about to announce, replacing an unanswered one.</summary>
    public static void SetPending(PlayerState player, PendingMatch match)
    {
        var slot = s_slots.GetValue(player, _ => new Slot());
        lock (slot.Gate) slot.Pending = match;
    }

 /// <summary>Removes and returns the match awaiting a 1559, or null when there is none.</summary>
    public static PendingMatch? TakePending(PlayerState player)
    {
        if (!s_slots.TryGetValue(player, out var slot)) return null;
        lock (slot.Gate)
        {
            var match = slot.Pending;
            slot.Pending = null;
            return match;
        }
    }

 /// <summary>Records the run started by a 1559 for the player's character, replacing an earlier unfinished one.</summary>
    public static void SetRun(PlayerState player, FinderRun run)
    {
        lock (s_runsGate) s_runs[player.CharacterId] = run;
    }

 /// <summary>
 /// The run started by the finder for <paramref name="player"/>'s character, or null when there is none or the
 /// character is no longer bound to its instance.
 /// </summary>
    public static FinderRun? GetRun(PlayerState player)
    {
        lock (s_runsGate)
        {
            if (!s_runs.TryGetValue(player.CharacterId, out var run)) return null;
            if (run.InstanceId is long instanceId && !DungeonInstanceRegistry.Shared.IsBound(instanceId, player.CharacterId))
            {
                s_runs.Remove(player.CharacterId);
                return null;
            }
            return run;
        }
    }

 /// <summary>
 /// Ends <paramref name="run"/> if it is still the character's current run and starts the cleared lifetime of
 /// its instance. Returns true when it was ended.
 /// </summary>
    public static bool TryEndRun(PlayerState player, FinderRun run)
    {
        lock (s_runsGate)
        {
            if (!s_runs.TryGetValue(player.CharacterId, out var current) || !ReferenceEquals(current, run)) return false;
            s_runs.Remove(player.CharacterId);
        }
        if (run.InstanceId is long instanceId)
            DungeonInstanceRegistry.Shared.MarkCleared(instanceId);
        return true;
    }
}
