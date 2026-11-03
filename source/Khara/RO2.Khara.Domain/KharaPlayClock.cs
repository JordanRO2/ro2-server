namespace RO2.Kharas.Domain;

/// <summary>
/// The arithmetic of a character's play clock: a play instant is a wall-clock instant with the paused intervals removed.
/// <paramref name="PlayAt"/> is the play instant at the last checkpoint (seed, resume or pause);
/// <paramref name="RunningSinceUtc"/> is the wall-clock instant the clock resumed at, or null while it is stopped.
/// The process-global clocks and their hooks stay in the host (KharaTitleClock).
/// </summary>
public readonly record struct PlayClock(DateTime PlayAt, DateTime? RunningSinceUtc)
{
    /// <summary>The play instant at <paramref name="wallNowUtc"/>: <see cref="PlayAt"/> plus the time run since resume.</summary>
    public DateTime At(DateTime wallNowUtc)
    {
        if (RunningSinceUtc is not { } since || wallNowUtc <= since)
            return PlayAt;
        return PlayAt + (wallNowUtc - since);
    }
}
