namespace RO2.Monster.Application;

/// <summary>
/// The respawn decision of a tick: which of the dead monsters that are ready to return (30 s after death, see
/// <c>MonsterRespawnRules</c>) come back now, and whether a returned monster may be announced. The host keeps the
/// fresh handle, the corpse bookkeeping, the live-state resets and the 2317 wire, and applies the decision one monster
/// at a time in its original order. Generic over the host's monster type so the host keeps its own object through
/// the re-keying.
/// </summary>
public static class RespawnPass
{
    /// <summary>Cap on respawns per tick: a map farmed hard trickles back instead of emitting one burst of frames.</summary>
    public const int MaxPerTick = 16;

    /// <summary>
    /// The ready monsters to respawn this tick: those on a populated map, the first <see cref="MaxPerTick"/> in the
    /// order <paramref name="ready"/> yields them. <paramref name="ready"/> is enumerated lazily and not at all when
    /// no map is populated.
    /// </summary>
    public static T[] Select<T>(IEnumerable<T> ready, IReadOnlyCollection<uint> populatedMaps, Func<T, uint> mapOf)
        => populatedMaps.Count == 0
            ? []
            : ready.Where(m => populatedMaps.Contains(mapOf(m))).Take(MaxPerTick).ToArray();

    /// <summary>
    /// Whether a respawned monster is announced: it needs a spawn row and an NPCInfo row for its asset; without
    /// one the client discards the actor and retries every later packet for it forever.
    /// </summary>
    public static bool MayAnnounce(bool hasSpawn, bool hasNpcInfoRow) => hasSpawn && hasNpcInfoRow;
}
