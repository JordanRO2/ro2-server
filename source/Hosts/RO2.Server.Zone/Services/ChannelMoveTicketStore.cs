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
/// The store is in-memory and process-local. The reconnect always reaches the same Zone process, so this is sufficient.
/// </para>
/// </remarks>
public sealed class ChannelMoveTicketStore
{
 /// <summary>Size in bytes of the reconnect token.</summary>
    public const int TokenBytes = 16;

 /// <summary>How long an unclaimed ticket remains valid.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

 /// <summary>A pending channel move.</summary>
 /// <param name="AccountId">Account that owns the move. It is written into the ticket and checked again on reconnect.</param>
 /// <param name="CharacterUid">The character being moved.</param>
 /// <param name="ChannelHandle">Target channel handle requested by the client; echoed back in the answer.</param>
 /// <param name="IssuedUtc">When the ticket was issued, used to enforce <see cref="Lifetime"/>.</param>
    public sealed record Ticket(uint AccountId, ulong CharacterUid, uint ChannelHandle, DateTimeOffset IssuedUtc);

    
    
    
    
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Ticket> _pending = new(StringComparer.Ordinal);

 /// <summary>Number of pending moves. Intended for diagnostics.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
                return _pending.Count;
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
        if (token.Length != TokenBytes)
            throw new ArgumentException($"channel-move token must be {TokenBytes} bytes", nameof(token));

        var key = Key(token);
        lock (_gate)
        {
            
            
            SweepExpired(now);
            RemoveAccountLocked(accountId);
            _pending[key] = new Ticket(accountId, characterUid, channelHandle, now);
        }
    }

 /// <summary>
 /// Claims the move that <paramref name="token"/> belongs to. Returns null when there is no such move, it has expired, it was revoked, or it was issued to a different account.
 /// </summary>
 /// <remarks>
 /// A ticket can be claimed only once. A successful claim also revokes any other ticket the account still holds.
 /// </remarks>
    public Ticket? Consume(ReadOnlySpan<byte> token, uint accountId)
        => Consume(token, accountId, DateTimeOffset.UtcNow);

 /// <summary>Claims a move at the given time. Intended for tests.</summary>
    internal Ticket? Consume(ReadOnlySpan<byte> token, uint accountId, DateTimeOffset now)
    {
        if (token.Length != TokenBytes)
            return null;

        var key = Key(token);
        lock (_gate)
        {
            
            
            
            if (!_pending.TryGetValue(key, out var ticket) || ticket.AccountId != accountId)
                return null;
            if (now - ticket.IssuedUtc > Lifetime)
            {
                _pending.Remove(key);
                return null;
            }

            _pending.Remove(key);
            
            
            RemoveAccountLocked(ticket.AccountId);
            return ticket;
        }
    }

 /// <summary>
 /// Revokes every pending ticket of <paramref name="accountId"/>, for example after a new login or a logout ends the account's move.
 /// </summary>
 /// <returns>The number of tickets revoked.</returns>
    public int RevokeAccount(uint accountId)
    {
        lock (_gate)
            return RemoveAccountLocked(accountId);
    }

    private int RemoveAccountLocked(uint accountId)
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

    private void SweepExpired(DateTimeOffset now)
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
