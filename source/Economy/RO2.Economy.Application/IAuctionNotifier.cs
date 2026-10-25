namespace RO2.Economy.Application;

/// <summary>
/// The in-world notices the auction flows push to online players (the 2693 system message: sold, won, outbid,
/// cancelled-with-bid, …). The host implements it over the player registry and the session broadcaster; the
/// economy application only raises the notice and never touches the wire.
/// </summary>
public interface IAuctionNotifier
{
    /// <summary>Sends a 2693 auction system notice of <paramref name="type"/> to <paramref name="charId"/> if
    /// they are online, carrying the article uid, its item template and an amount.</summary>
    Task SendSystemMessageAsync(uint charId, uint type, ulong articleUid, uint itemId, long amount);
}
