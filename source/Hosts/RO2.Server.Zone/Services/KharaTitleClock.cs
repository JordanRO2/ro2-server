namespace RO2.Server.Zone.Services;

/// <summary>
/// Play clock of each character, which runs the Khara title timers (<see cref="KharaTitleStore"/>). The clock runs while the
/// character is in the world on a map whose Map_Type is not <see cref="PausedMapType"/>, and stops while the character is
/// offline or on a paused map.
/// </summary>
/// <remarks>
/// A play instant is a wall-clock instant with the paused intervals removed. A character this process has not seen reads
/// the wall clock. The stored title timers are instants on this same axis.
/// </remarks>
internal static class KharaTitleClock
{
 /// <summary>Map_Type of the maps on which the play clock stops.</summary>
    internal const byte PausedMapType = 3;

    private sealed class Entry
    {
 /// <summary>The play instant at the last checkpoint (seed, resume or pause).</summary>
        public DateTime PlayAt;

 /// <summary>The wall-clock instant the clock resumed at, or null while it is stopped.</summary>
        public DateTime? RunningSinceUtc;
    }

    private static readonly Lock Gate = new();
    private static readonly Dictionary<uint, Entry> Clocks = new();

 /// <summary>
 /// The character's play instant at <paramref name="wallNowUtc"/>. A character this process does not know reads
 /// <paramref name="wallNowUtc"/>.
 /// </summary>
    internal static DateTime PlayNow(uint characterId, DateTime wallNowUtc)
    {
        lock (Gate)
        {
            return Clocks.TryGetValue(characterId, out var e) ? PlayNowLocked(e, wallNowUtc) : wallNowUtc;
        }
    }

 /// <summary>Whether the character's clock is running. False for a character this process does not know.</summary>
    internal static bool IsRunning(uint characterId)
    {
        lock (Gate)
        {
            return Clocks.TryGetValue(characterId, out var e) && e.RunningSinceUtc is not null;
        }
    }

 /// <summary>
 /// Gives the character a stopped clock at <paramref name="storedPlayUtc"/>, or at <paramref name="wallNowUtc"/> when no
 /// stored instant exists. An existing clock is kept, and only moved forward to a later stored instant.
 /// </summary>
    internal static void Seed(uint characterId, DateTime? storedPlayUtc, DateTime wallNowUtc)
    {
        lock (Gate)
        {
            if (!Clocks.TryGetValue(characterId, out var e))
            {
                Clocks[characterId] = new Entry { PlayAt = storedPlayUtc ?? wallNowUtc };
                return;
            }
            if (storedPlayUtc is { } stored && stored > PlayNowLocked(e, wallNowUtc))
            {
                e.PlayAt = stored;
                if (e.RunningSinceUtc is not null) e.RunningSinceUtc = wallNowUtc;
            }
        }
    }

 /// <summary>Starts the character's clock at <paramref name="wallNowUtc"/>. Does nothing while it runs.</summary>
    internal static void Resume(uint characterId, DateTime wallNowUtc)
    {
        lock (Gate)
        {
            if (!Clocks.TryGetValue(characterId, out var e))
                Clocks[characterId] = e = new Entry { PlayAt = wallNowUtc };
            e.RunningSinceUtc ??= wallNowUtc;
        }
    }

 /// <summary>Stops the character's clock at <paramref name="wallNowUtc"/>. Does nothing while it is stopped or unknown.</summary>
    internal static void Pause(uint characterId, DateTime wallNowUtc)
    {
        lock (Gate)
        {
            if (Clocks.TryGetValue(characterId, out var e) && e.RunningSinceUtc is not null)
            {
                e.PlayAt = PlayNowLocked(e, wallNowUtc);
                e.RunningSinceUtc = null;
            }
        }
    }

 /// <summary>Map-load hook: stops the clock on a paused map type and runs it on any other.</summary>
    internal static void OnMapLoaded(uint characterId, byte? mapType, DateTime wallNowUtc)
    {
        if (mapType == PausedMapType)
            Pause(characterId, wallNowUtc);
        else
            Resume(characterId, wallNowUtc);
    }

 /// <summary>Removes the character's clock.</summary>
    internal static void Forget(uint characterId)
    {
        lock (Gate)
        {
            Clocks.Remove(characterId);
        }
    }

    private static DateTime PlayNowLocked(Entry e, DateTime wallNowUtc)
    {
        if (e.RunningSinceUtc is not { } since || wallNowUtc <= since)
            return e.PlayAt;
        return e.PlayAt + (wallNowUtc - since);
    }
}
