using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1418 ReqChangeCharacterInGame - Switch character without full logout</summary>
public class ChangeCharacterHandler : ZoneRmiHandlerBase<ChangeCharacterHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqChangeCharacterInGame;
    public override string Name => "ChangeCharacter";
    public ChangeCharacterHandler(IServiceScopeFactory s, ILogger<ChangeCharacterHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogInformation("{Name} requests character change", player.Name);

        
        
        
        
        
        await IllusionPyramidService.CloseOnDepartureAsync(session, player, services, Logger, ct);

        
        
        
        
        
        
        
        await RidingService.DropAsync(session, player, services);

        var spawnService = services.GetRequiredService<PlayerSpawnService>();
        await spawnService.NotifyPlayerDespawnAsync(player);

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        await session.SendInternalRmiAsync(2938, SessionExitPackets.Ack2938);

        
        
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPC_Out,
            PlayerSpawnService.BuildActorOutList(player.EntityId, new[] { player.EntityId }));

        await session.SendInternalRmiAsync(2312, SessionExitPackets.ChangePc2312);

        
        
        
        
        
        
        
        
        
        
        
        
        try
        {
            await services.GetRequiredService<PartyService>().NotifyMemberSwitchedCharacterAsync(player);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Session {Id}: failed to notify the party that '{Name}' left for character select",
                session.SessionId, player.Name);
        }

        
        
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

        Logger.LogInformation("Session {Id}: acked 1418 (2938/2505/2312) and released '{Name}'",
            session.SessionId, player.Name);
    }
}
