namespace RO2.Quests.Domain;

/// <summary>
/// The tracker filter shared by the 1540 save and the 2779 load: only quests the player still holds can be pinned.
/// Pure; no state, no persistence, no wire.
/// </summary>
public static class TraceRules
{
    /// <summary>
    /// The requested quest ids that are active, as a new set. Keeps the requested enumeration order (the client
    /// tolerates it), which a copy of the sequence preserves.
    /// </summary>
    public static HashSet<uint> Normalize(IEnumerable<uint> requested, ISet<uint> active)
    {
        var traced = new HashSet<uint>();
        foreach (var q in requested) if (active.Contains(q)) traced.Add(q);
        return traced;
    }
}
