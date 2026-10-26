using RO2.Worlds.Domain;

namespace RO2.Worlds.Application;

/// <summary>
/// The pending channel-move tickets, as the channel-move flows see them: issued when the move is answered (1420 to
/// 2556), claimed once when the client reconnects (1421), and revoked when the account's move ends. The host implements
/// it as a process-local, thread-safe store over <see cref="ChannelMoveTicketLedger"/>; the reconnect always reaches
/// the same Zone process, so the live state stays in the host.
/// </summary>
public interface IChannelMoveTicketStore
{
    /// <summary>Number of pending moves. Only a hint: tickets expire lazily.</summary>
    int PendingCount { get; }

    /// <summary>Records a pending move when the ticket is issued. Any earlier ticket of the same account is revoked.</summary>
    /// <exception cref="ArgumentException">The token is not <see cref="ChannelMoveTicketLedger.TokenBytes"/> bytes.</exception>
    void Issue(ReadOnlySpan<byte> token, uint accountId, ulong characterUid, uint channelHandle);

    /// <summary>
    /// Claims the move that <paramref name="token"/> belongs to; null when there is none, it expired, it was revoked or
    /// it belongs to another account. A ticket is claimed only once, and a claim revokes the account's other tickets.
    /// </summary>
    ChannelMoveTicket? Consume(ReadOnlySpan<byte> token, uint accountId);

    /// <summary>Revokes every pending ticket of <paramref name="accountId"/>; returns how many were revoked.</summary>
    int RevokeAccount(uint accountId);
}
