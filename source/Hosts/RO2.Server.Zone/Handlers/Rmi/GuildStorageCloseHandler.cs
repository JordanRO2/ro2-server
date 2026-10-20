using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1490 ReqCloseGuildStorage: closes the guild storage window.</summary>
public class GuildStorageCloseHandler : ZoneRmiHandlerBase<GuildStorageCloseHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqCloseGuildStorage;
    public override string Name => "GuildStorageClose";
    public GuildStorageCloseHandler(IServiceScopeFactory s, ILogger<GuildStorageCloseHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogDebug("{Name} closes guild storage", player.Name);
        
        
        GuildVaultWindows.Close(session.SessionId);
        await Task.CompletedTask;
    }
}
