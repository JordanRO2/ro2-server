using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1301 SendPacket (ByteArray). Generic packet relay that only logs.</summary>
public class SendPacketHandler : ZoneRmiHandlerBase<SendPacketHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.SendPacket;
    public override string Name => "SendPacket";

    public SendPacketHandler(IServiceScopeFactory s, ILogger<SendPacketHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogDebug("{Name}: SendPacket ({Size}B)", player.Name, payload.Length);
        return Task.CompletedTask;
    }
}
