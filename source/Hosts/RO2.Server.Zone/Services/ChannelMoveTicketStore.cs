using RO2.Worlds.Application;
using RO2.Worlds.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Holds the pending hand-off for a channel move. The move starts with the channel move request (1420), the server answers with the move ticket (2556), and the client reconnects and presents the ticket (1421).
/// </summary>
/// <remarks>
/// <para>
/// A channel move spans two connections. The reconnect lands on a new session that has no account or character yet, so the account, character and target channel are stored here, keyed by the ticket token the client echoes back.
/// </para>
/// <para>
/// The account is always taken from the issued ticket, never from the identifier sent in the packet. Each account has at most one live ticket: issuing a new ticket, or claiming one, revokes the account's other tickets.
/// </para>
/// <para>
/// The store is in-memory and process-local. The reconnect always reaches the same Zone process, so this is sufficient. The ticket rules (lifetime, one live ticket per account, single use, expiry) live in <see cref="ChannelMoveTicketLedger"/>; this host store only serialises access and supplies the clock.
/// </para>
/// </remarks>
public sealed class ChannelMoveTicketStore : IChannelMoveTicketStore
{
    /// <summary>Size in bytes of the reconnect token.</summary>
    public const int TokenBytes = ChannelMoveTicketLedger.TokenBytes;

    /// <summary>How long an unclaimed ticket remains valid.</summary>
    public static readonly TimeSpan Lifetime = ChannelMoveTicketLedger.Lifetime;

    private readonly Lock _gate = new();
    private readonly ChannelMoveTicketLedger _ledger = new();

    /// <summary>Number of pending moves. Intended for diagnostics.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
                return _ledger.PendingCount;
        }
    }

    /// <summary>
    /// Records a pending move when the ticket is issued. Any earlier ticket of the same account is revoked.
    /// </summary>
    public void Issue(ReadOnlySpan<byte> token, uint accountId, ulong characterUid, uint channelHandle)
        => Issue(token, accountId, characterUid, channelHandle, DateTimeOffset.UtcNow);

    /// <summary>Records a pending move at the given time. Intended for tests.</summary>
    internal void Issue(ReadOnlySpan<byte> token, uint accountId, ulong characterUid, uint channelHandle,
        DateTimeOffset now)
    {
        lock (_gate)
            _ledger.Issue(token, accountId, characterUid, channelHandle, now);
    }

    /// <summary>
    /// Claims the move that <paramref name="token"/> belongs to. Returns null when there is no such move, it has expired, it was revoked, or it was issued to a different account.
    /// </summary>
    /// <remarks>
    /// A ticket can be claimed only once. A successful claim also revokes any other ticket the account still holds.
    /// </remarks>
    public ChannelMoveTicket? Consume(ReadOnlySpan<byte> token, uint accountId)
        => Consume(token, accountId, DateTimeOffset.UtcNow);

    /// <summary>Claims a move at the given time. Intended for tests.</summary>
    internal ChannelMoveTicket? Consume(ReadOnlySpan<byte> token, uint accountId, DateTimeOffset now)
    {
        lock (_gate)
            return _ledger.Consume(token, accountId, now);
    }

    /// <summary>
    /// Revokes every pending ticket of <paramref name="accountId"/>, for example after a new login or a logout ends the account's move.
    /// </summary>
    /// <returns>The number of tickets revoked.</returns>
    public int RevokeAccount(uint accountId)
    {
        lock (_gate)
            return _ledger.RemoveAccount(accountId);
    }
}
