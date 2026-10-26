namespace RO2.Worlds.Domain;

/// <summary>A pending channel move.</summary>
/// <param name="AccountId">Account that owns the move. It is written into the ticket and checked again on reconnect.</param>
/// <param name="CharacterUid">The character being moved.</param>
/// <param name="ChannelHandle">Target channel handle requested by the client; echoed back in the answer.</param>
/// <param name="IssuedUtc">When the ticket was issued, used to enforce <see cref="ChannelMoveTicketLedger.Lifetime"/>.</param>
public sealed record ChannelMoveTicket(uint AccountId, ulong CharacterUid, uint ChannelHandle, DateTimeOffset IssuedUtc);

/// <summary>
/// The pure bookkeeping of the pending channel moves: the ticket lifetime, the one-live-ticket-per-account rule, the
/// single-use claim and the lazy expiry. It holds the pending tickets in a plain dictionary and takes "now" as an
/// argument; it is not thread-safe, so the host store that owns the live state serialises every call and supplies the
/// clock.
/// </summary>
/// <remarks>
/// A channel move spans two connections, so the account, character and target channel are kept by the ticket token the
/// client echoes back. The account is always taken from the issued ticket, never from the packet. Each account has at
/// most one live ticket: issuing a new ticket, or claiming one, revokes the account's other tickets.
/// </remarks>
public sealed class ChannelMoveTicketLedger
{
    /// <summary>Size in bytes of the reconnect token.</summary>
    public const int TokenBytes = 16;

    /// <summary>How long an unclaimed ticket remains valid.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, ChannelMoveTicket> _pending = new(StringComparer.Ordinal);

    /// <summary>Number of pending moves.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>
    /// Records a pending move at <paramref name="now"/>. Expired tickets are swept first, then any earlier ticket of the
    /// same account is revoked.
    /// </summary>
    /// <exception cref="ArgumentException">The token is not <see cref="TokenBytes"/> bytes.</exception>
    public void Issue(ReadOnlySpan<byte> token, uint accountId, ulong characterUid, uint channelHandle, DateTimeOffset now)
    {
        if (token.Length != TokenBytes)
            throw new ArgumentException($"channel-move token must be {TokenBytes} bytes", nameof(token));

        var key = Key(token);
        SweepExpired(now);
        RemoveAccount(accountId);
        _pending[key] = new ChannelMoveTicket(accountId, characterUid, channelHandle, now);
    }

    /// <summary>
    /// Claims the move that <paramref name="token"/> belongs to. Returns null when there is no such move, it has expired,
    /// it was revoked, or it was issued to a different account.
    /// </summary>
    /// <remarks>
    /// A ticket can be claimed only once. A successful claim also revokes any other ticket the account still holds. A
    /// claim by the wrong account neither takes the ticket nor revokes anything.
    /// </remarks>
    public ChannelMoveTicket? Consume(ReadOnlySpan<byte> token, uint accountId, DateTimeOffset now)
    {
        if (token.Length != TokenBytes)
            return null;

        var key = Key(token);
        if (!_pending.TryGetValue(key, out var ticket) || ticket.AccountId != accountId)
            return null;
        if (now - ticket.IssuedUtc > Lifetime)
        {
            _pending.Remove(key);
            return null;
        }

        _pending.Remove(key);
        RemoveAccount(ticket.AccountId);
        return ticket;
    }

    /// <summary>Revokes every pending ticket of <paramref name="accountId"/>.</summary>
    /// <returns>The number of tickets revoked.</returns>
    public int RemoveAccount(uint accountId)
    {
        List<string>? doomed = null;
        foreach (var (key, ticket) in _pending)
        {
            if (ticket.AccountId == accountId)
                (doomed ??= []).Add(key);
        }

        if (doomed is null)
            return 0;
        foreach (var key in doomed)
            _pending.Remove(key);
        return doomed.Count;
    }

    /// <summary>Drops every ticket older than <see cref="Lifetime"/> at <paramref name="now"/>.</summary>
    public void SweepExpired(DateTimeOffset now)
    {
        List<string>? expired = null;
        foreach (var (key, ticket) in _pending)
        {
            if (now - ticket.IssuedUtc > Lifetime)
                (expired ??= []).Add(key);
        }

        if (expired is null)
            return;
        foreach (var key in expired)
            _pending.Remove(key);
    }

    private static string Key(ReadOnlySpan<byte> token) => Convert.ToHexString(token);
}
