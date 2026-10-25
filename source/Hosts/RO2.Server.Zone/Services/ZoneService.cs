using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Enums;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Core zone server functionality: authentication, spawning, world state.
/// </summary>
public class ZoneService
{
    private readonly PlayerStateManager _playerManager;
    private readonly BroadcastService _broadcast;
    private readonly ILogger<ZoneService> _logger;

    public ZoneService(PlayerStateManager playerManager, BroadcastService broadcast, ILogger<ZoneService> logger)
    {
        _playerManager = playerManager;
        _broadcast = broadcast;
        _logger = logger;
    }

    public async Task AuthenticatePlayerAsync(
        ProudSession session,
        uint characterId,
        ICharacterRepository characters)
    {
        var character = await characters.GetByIdAsync(characterId);
        if (character == null)
        {
            _logger.LogWarning("Character {CharacterId} not found for session {SessionId}",
                characterId, session.SessionId);
            return;
        }

        var player = new PlayerState
        {
            SessionId = session.SessionId,
            CharacterId = character.Id,
            AccountId = character.AccountId,
            Name = character.Name,
            Job = character.Job,
            Level = character.Level,
            MapId = character.MapId,
            X = character.X,
            Y = character.Y,
            Z = character.Z,
            Hp = character.Hp,
            MaxHp = character.MaxHp,
            Mp = character.Mp,
            MaxMp = character.MaxMp
        };

        _playerManager.AddPlayer(session.SessionId, player);
        session.CharacterId = characterId;
        session.State = SessionState.InGame;

        _logger.LogInformation("Player {Name} entered the world (Map: {MapId}, Level: {Level})",
            player.Name, player.MapId, player.Level);

        // OnPC_In (2504) is deliberately not sent here. 2504 carries a variable-length remote-player
        // record, and the flat record that used to be written here does not match it (a malformed 2504
        // crashes the receiving client). The correct 2504 builder is used by the live world-entry path:
        // PlayerSpawnService.NotifyPlayerSpawnAsync (called from LoginPCHandler / ChatHandler) sends it
        // via SendInternalRmiAsync. This AuthenticatePlayerAsync method is not wired into the server
        // (Program.cs only wires HandlePlayerDisconnectAsync), so a send here would be dead code.
        // Player state is registered above.
    }

    public async Task HandlePlayerDisconnectAsync(uint sessionId, IServiceProvider? services = null)
    {
        // The ride ends with the connection (a dropped connection, and the close after a 1302 logout or a 1420 channel
        // move): the client is gone, so this is state only, and it comes before the RemovePlayer test because 1302
        // removes the player itself first. MountService is keyed by session id, so an entry would otherwise live for
        // the life of the process. This runs on socket close only (Program.cs SessionDisconnected), so it does NOT cover
        // a 1418 character change: that connection stays open for the next character. The ride is dropped on the 1418,
        // ahead of its 2938 (RidingService, CHARACTER CHANGE), and RidingService never treats an entry an earlier
        // PlayerState of the session left as the next character's ride.
        MountService.Dismount(sessionId);

        // A live trade ends with the connection too: nothing is escrowed, so cancelling is forgetting the offers,
        // and the partner's window is closed. Like the ride, it comes before the RemovePlayer test (1302 removes the
        // player first), and a swap already running dropped its session itself, so this finds nothing then.
        await CancelLiveTradeAsync(sessionId);

        if (_playerManager.RemovePlayer(sessionId, out var player) && player != null)
        {
            // Notify nearby players about despawn.
            // The body is a 16-byte actor-out-list naming the actor handle the other clients learned
            // from OnPC_In (2504), not the session id, so departed characters are removed from
            // everyone else's screen.
            var outList = PlayerSpawnService.BuildActorOutList(player.EntityId, new[] { player.EntityId });
            await _broadcast.BroadcastToMapAsync(player.MapId, ZoneS2COpcodes.OnPC_Out,
                writer => writer.WriteBytes(outList), excludeSessionId: sessionId);

            // Party members anywhere (not just this map) are told the member is gone for now: 2360
            // kind 1. Only a session that ends WITHOUT an exit RMI gets here with its PlayerState still
            // in place: 1302 logout and 1418 character change send it themselves and remove the player
            // first, so RemovePlayer above is false for them. What is left is a dropped connection and a
            // 1420 channel move, and the move must not lock anyone (see NotifyPartyIfStillGoneAsync), so
            // the send is deferred until it is clear the character is not coming back. Detached so the
            // final save below is not held up by it.
            if (services != null)
                _ = NotifyPartyIfStillGoneAsync(player, services);

            // Save character state to DB
            if (services != null)
            {
                try
                {
                    // Boundary flush: persist the player's final snapshot synchronously through the same
                    // real-time write-behind service, so a logout never rolls back the last live change.
                    await services.GetRequiredService<PersistenceService>().FlushAsync(player);
                    _logger.LogDebug("Flushed final state for {Name}: Map={Map} Pos=({X},{Y},{Z}) HP={Hp} Gold={Gold}",
                        player.Name, player.MapId, player.X, player.Y, player.Z, player.Hp, player.Gold);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to save state for {Name}", player.Name);
                }

                // RemovePlayer stopped the Khara title play clock (unless the character is back on another connection,
                // as after a channel move or a hand-off); store where it stands for the next login
                // (KharaTitleStore.PreloadAsync). A drop is an exit like a 1302: retail's clock stands still offline.
                try
                {
                    await services.GetRequiredService<KharaTitleStore>().SavePlayClockAsync(player.CharacterId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to store the Khara title play clock of {Name}", player.Name);
                }
            }

            _logger.LogInformation("Player {Name} left the world. {Count} remain.",
                player.Name, _playerManager.PlayerCount);
        }
    }

    /// <summary>
    /// Drops the live trade (and pending request) of a session that is going away and closes the partner's window with
    /// 2388 [its OWN handle][0], the same frame a cancel sends (TradeConfirmHandler.SendCancelledAsync). A trade whose
    /// swap has latched is left alone: it is not cancellable any more and its own answers close the windows.
    /// </summary>
    private async Task CancelLiveTradeAsync(uint sessionId)
    {
        var trade = TradeSessionService.Instance.Cancel(sessionId);
        if (trade == null || trade.Completing) return;

        var partnerSession = trade.Other(sessionId);
        try
        {
            var partner = _broadcast.GetSession(partnerSession);
            if (partner != null)
                await partner.SendRmiAsync(ZoneS2COpcodes.OnConfirmTradePC,
                    TradePackets.BuildOnConfirmTradePC(trade.HandleFor(partnerSession), 0));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to close the trade window of session {Partner} after session {Session} left",
                partnerSession, sessionId);
        }
    }

    /// <summary>Poll interval while a dropped member's 2360 kind 1 waits for the character to re-enter.</summary>
    private static readonly TimeSpan PartyLockPoll = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How long to keep waiting after no channel move is pending any more: the gap between a 1421 claiming
    /// its ticket and the 1417 that re-adds the PlayerState. 1417 follows the 2557 answer to 1421 almost
    /// immediately (see ChannelMoveTicketStore); the rest of the window covers LoginPCHandler's database
    /// reads before AddPlayer.
    /// </summary>
    private static readonly TimeSpan PartyLockReEntrySettle = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Sends 2360 OnPartyNotifyOut kind 1 (lock) for a member whose session ended without an exit RMI,
    /// unless the same character is back in the world first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The case this exists for is the 1420 channel move. ChannelMoveHandler leaves the PlayerState in
    /// place and the client closes this connection about 2 ms after 2556, so the move lands here exactly
    /// like a drop, but the mover must not be locked. The reconnected session has a new session id that
    /// the party does not know, so nothing would ever unlock the member again, and the others would see
    /// the mover locked for good.
    /// </para>
    /// <para>
    /// So the lock waits while any channel-move ticket is still unclaimed, at most for the ticket
    /// lifetime, plus a short settle for the 1421 -> 1417 gap, and is dropped if a PlayerState with the
    /// same character id appears in that time. The ticket is issued before 2556 goes out, so a mover's
    /// ticket is pending when its old connection closes, unless the new connection has already claimed
    /// it, and then the character is back or about to be. With no move in flight a real drop is locked after
    /// the settle alone. PendingCount is only a hint: tickets expire lazily, so a stale one can make a
    /// drop wait the full lifetime, never skip the lock.
    /// </para>
    /// </remarks>
    private async Task NotifyPartyIfStillGoneAsync(PlayerState departed, IServiceProvider services)
    {
        try
        {
            var tickets = services.GetService<ChannelMoveTicketStore>();
            var start = DateTime.UtcNow;
            var hardDeadline = start + ChannelMoveTicketStore.Lifetime + PartyLockReEntrySettle;
            DateTime? settleDeadline = null;

            while (true)
            {
                if (_playerManager.GetPlayerByCharacterId(departed.CharacterId) != null)
                {
                    _logger.LogInformation("{Name} re-entered the world after the session ended (channel move); "
                        + "party not told to lock the member", departed.Name);
                    return;
                }

                var now = DateTime.UtcNow;
                if (settleDeadline == null && (tickets == null || tickets.PendingCount == 0))
                    settleDeadline = now + PartyLockReEntrySettle;
                if (now >= hardDeadline || (settleDeadline != null && now >= settleDeadline))
                    break;

                await Task.Delay(PartyLockPoll);
            }

            // Uses the PlayerState removed from the manager, since GetPlayer is already null for it. The
            // departed session is gone from the session table, so only the other members get the 2360.
            await services.GetRequiredService<PartyService>().NotifyMemberOfflineAsync(departed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to notify the party that {Name} went offline", departed.Name);
        }
    }
}
