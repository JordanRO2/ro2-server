using Microsoft.Extensions.Logging;
using RO2.Economy.Application;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Host implementation of <see cref="IAuctionNotifier"/>: it resolves the online player and session and sends the
/// 2693 <c>NfyAuctionSystemMessage</c> packet, swallowing and logging a send failure (a notice is best-effort).
/// </summary>
public sealed class AuctionNotifierAdapter(PlayerStateManager players, BroadcastService broadcast, ILogger log) : IAuctionNotifier
{
    /// <inheritdoc />
    public async Task SendSystemMessageAsync(uint charId, uint type, ulong articleUid, uint itemId, long amount)
    {
        try
        {
            var online = players.GetPlayerByCharacterId(charId);
            if (online == null) return;
            var session = broadcast.GetSession(online.SessionId);
            if (session == null) return;
            await session.SendRmiAsync(RO2.Network.Protocol.ZoneS2COpcodes.NfyAuctionSystemMessage,
                AuctionPackets.BuildSystemMessage2693(online.EntityId, type, articleUid, itemId, amount));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Auction notice {Type} failed for char {Char}", type, charId);
        }
    }
}
