namespace RO2.Application.Interfaces;

/// <summary>
/// Per-character guild-departure state that outlives guild membership: when a character last left a guild
/// of its own accord (the start of the rejoin cooldown) and whether an offline kick still has to be told at
/// its next world entry. Loaded once from storage and kept in memory; reads are synchronous.
/// </summary>
public interface IGuildDepartureRepository
{
 /// <summary>Load every stored departure exactly once. Safe to call on every message; readers and writers
 /// call it first, because nothing else loads the state after a restart.</summary>
    Task InitAsync();

 /// <summary>When <paramref name="charId"/> last left a guild (UTC), or null.</summary>
    DateTime? LeftUtcOf(uint charId);

 /// <summary>Record that <paramref name="charId"/> left its guild at <paramref name="whenUtc"/> — the
 /// moment its rejoin cooldown starts.</summary>
    Task RecordLeaveAsync(uint charId, DateTime whenUtc);

 /// <summary>Record that <paramref name="charId"/> was kicked while offline, so its next world entry
 /// carries the 2570 notice.</summary>
    Task MarkKickNoticeAsync(uint charId);

 /// <summary>Whether <paramref name="charId"/> has an offline-kick notice waiting, clearing it in the same
 /// step so the notice goes out once (two racing world entries cannot both see it).</summary>
    Task<bool> TakeKickNoticeAsync(uint charId);
}
