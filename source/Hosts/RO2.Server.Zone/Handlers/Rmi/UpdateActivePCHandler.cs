using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1591 ReqUpdateActivePC (Empty) - Notify server that player is active</summary>
public class UpdateActivePCHandler : ZoneRmiHandlerBase<UpdateActivePCHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqUpdateActivePC;
    public override string Name => "UpdateActivePC";

    public UpdateActivePCHandler(IServiceScopeFactory s, ILogger<UpdateActivePCHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        player.LastActivity = DateTime.UtcNow;
        return Task.CompletedTask;
    }
}
