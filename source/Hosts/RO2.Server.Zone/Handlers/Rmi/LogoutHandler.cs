using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1302 (RequestLogout): removes the player from the world and notifies nearby players.
/// </summary>
public class LogoutHandler : ZoneRmiHandlerBase<LogoutHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.RequestLogout;
    public override string Name => "Logout";

    public LogoutHandler(
        IServiceScopeFactory scopeFactory,
        ILogger<LogoutHandler> logger,
        PlayerStateManager playerManager,
        BroadcastService broadcast)
        : base(scopeFactory, logger, playerManager, broadcast)
    {
    }

    protected override async Task HandleAsync(
        ProudSession session,
        ReadOnlyMemory<byte> payload,
        PlayerState player,
        IServiceProvider services,
        CancellationToken ct)
    {
        Logger.LogInformation("Session {Id}: Player '{Name}' requesting logout",
            session.SessionId, player.Name);

        
        
        
        
        
        
        await IllusionPyramidService.CloseOnDepartureAsync(session, player, services, Logger, ct);

        
        
        
        
        
        var outList = PlayerSpawnService.BuildActorOutList(player.EntityId, new[] { player.EntityId });
        await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.OnPC_Out,
            writer => writer.WriteBytes(outList));

        
        
        
        
        
        
        
        
        
        
        await session.SendInternalRmiAsync(2305, SessionExitPackets.DisconnectNotify2305);
        await session.SendInternalRmiAsync(2938, SessionExitPackets.Ack2938);

        
        
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPC_Out, outList);

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        try
        {
            await services.GetRequiredService<PartyService>().NotifyMemberOfflineAsync(player);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Session {Id}: failed to notify the party that '{Name}' logged out",
                session.SessionId, player.Name);
        }

        
        
        
        await services.GetRequiredService<PersistenceService>().FlushAsync(player);

        
        
        
        PlayerManager.RemovePlayer(session.SessionId, out _);

        
        
        
        try
        {
            await services.GetRequiredService<KharaTitleStore>().SavePlayClockAsync(player.CharacterId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Session {Id}: could not store the Khara title play clock of '{Name}'",
                session.SessionId, player.Name);
        }

        Logger.LogInformation("Session {Id}: Player '{Name}' logged out. {Count} players remain.",
            session.SessionId, player.Name, PlayerManager.PlayerCount);

        
        
        
        
        
        
        
        
        
        
        
        var accountId = session.AccountId ?? player.AccountId;
        session.AccountId = null;

        
        
        
        
        await ZoneAuthHandler.ReleaseOnlineAccountAtLogoutAsync(session, accountId, services, Logger);
    }
}
