using RO2.Worlds.Domain;

namespace RO2.Worlds.Application;

/// <summary>
/// The decision on a 1420 ReqChannelMove: either a refusal to answer with a 2556 result code, or the channel to move to.
/// </summary>
/// <param name="RefusalCode">The 2556 result code when the move is refused (a <see cref="ChannelMoveRules"/> value); null when it proceeds.</param>
/// <param name="TargetChannel">The channel handle to issue the ticket for; the one the client requested. Meaningful only when the move proceeds.</param>
public readonly record struct ChannelMoveDecision(ushort? RefusalCode, uint TargetChannel)
{
    /// <summary>True when the move is refused and no ticket may be issued.</summary>
    public bool IsRefused => RefusalCode.HasValue;
}

/// <summary>The outcome of presenting a ticket on a 1421 ReqChannelMoveLogin: the ticket it redeems, or none.</summary>
/// <param name="Ticket">The claimed move; null when there is no pending move for the token and account.</param>
public readonly record struct ChannelMoveRedemption(ChannelMoveTicket? Ticket)
{
    /// <summary>True when a pending move was claimed.</summary>
    public bool IsAccepted => Ticket is not null;
}

/// <summary>
/// The decisions of the channel-move flow (1420 ReqChannelMove, 1421 ReqChannelMoveLogin), over the pure
/// <see cref="ChannelMoveRules"/> and the <see cref="IChannelMoveTicketStore"/> port. Each is a thin decide: the host
/// performs the token generation, the ticket issue, the illusion-pyramid close, the persistence flush, the despawn, every
/// send and the channel-presence write in its own fixed order.
/// </summary>
public static class ChannelMoveUseCase
{
    /// <summary>
    /// Decides a 1420 for <paramref name="requested"/>: refused when it is the account's <paramref name="current"/> channel
    /// or not among <paramref name="channelIds"/>, otherwise a move to the requested channel. A refusal is decided before
    /// any token is issued.
    /// </summary>
    public static ChannelMoveDecision Decide(uint requested, uint? current, IEnumerable<uint> channelIds)
        => new(ChannelMoveRules.RefusalCode(requested, current, channelIds), requested);

    /// <summary>
    /// Claims, for a 1421, the move that <paramref name="token"/> belongs to. None when there is no such move, it
    /// expired, it was revoked, it was already claimed, or it was issued to another account; a claim revokes the
    /// account's other tickets (<see cref="IChannelMoveTicketStore.Consume"/>).
    /// </summary>
    public static ChannelMoveRedemption Redeem(IChannelMoveTicketStore tickets, ReadOnlySpan<byte> token, uint accountId)
        => new(tickets.Consume(token, accountId));
}
