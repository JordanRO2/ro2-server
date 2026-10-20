using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1659 (ReqTRoomOut). Leaves a tournament room. The body is empty. This server does not run tournament
/// rooms, so there is never a room to leave and nothing is sent.
/// </summary>
public class TRoomOutHandler : ZoneRmiHandlerBase<TRoomOutHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqTRoomOut;
    public override string Name => "TRoomOut";
    public TRoomOutHandler(IServiceScopeFactory s, ILogger<TRoomOutHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogInformation("{Name} asks to leave a tournament room, but none is running", player.Name);
        return Task.CompletedTask;
    }
}
