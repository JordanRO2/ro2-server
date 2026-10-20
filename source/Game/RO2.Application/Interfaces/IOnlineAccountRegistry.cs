using RO2.Domain.Entities;

namespace RO2.Application.Interfaces;

/// <summary>
/// Which accounts hold a Zone connection right now, shared by the Login and Zone processes through the
/// database. Login reads it to refuse a second login of an online account (12502 code 1008) and to ask the
/// Zone to drop the first one; the Zone writes it at 1303, keeps it alive while the account is in the world,
/// hands it to the connection a 1420 channel move reconnects on, and removes it at a 1302 logout. Liveness
/// is a heartbeat (<see cref="OnlineAccountRules.StaleAfter"/>), see <see cref="OnlineAccountRules"/>.
/// </summary>
public interface IOnlineAccountRegistry
{
 /// <summary>Zone, at 1303: this connection now holds the account (replacing any older entry).</summary>
    Task ClaimAsync(uint accountId, string owner, CancellationToken ct = default);

 /// <summary>
 /// Zone heartbeat: refreshes the entry if <paramref name="owner"/> still holds it and returns it, or
 /// returns null when another connection (or nobody) holds the account now.
 /// </summary>
    Task<OnlineAccountEntry?> HeartbeatAsync(uint accountId, string owner, CancellationToken ct = default);

 /// <summary>
 /// Zone, after a channel move: hands the entry from the closed connection <paramref name="fromOwner"/>
 /// to <paramref name="toOwner"/>. Only if <paramref name="fromOwner"/> still holds it, so a newer 1303
 /// claim is never overwritten, and a pending <see cref="OnlineAccountEntry.KickRequestedAt"/> carries
 /// over. Returns whether the entry moved.
 /// </summary>
    Task<bool> TransferAsync(uint accountId, string fromOwner, string toOwner, CancellationToken ct = default);

 /// <summary>Removes the entry if <paramref name="owner"/> still holds it.</summary>
    Task ReleaseAsync(uint accountId, string owner, CancellationToken ct = default);

 /// <summary>
 /// Removes the entry if any of <paramref name="owners"/> holds it, in one conditional delete (1302
 /// logout); a newer 1303 claim by another connection is never removed.
 /// </summary>
    Task ReleaseAnyAsync(uint accountId, IReadOnlyCollection<string> owners, CancellationToken ct = default);

 /// <summary>The entry for an account, or null.</summary>
    Task<OnlineAccountEntry?> GetAsync(uint accountId, CancellationToken ct = default);

 /// <summary>Login: asks the connection that holds the account to drop it (first request wins).</summary>
    Task RequestKickAsync(uint accountId, string owner, CancellationToken ct = default);
}
