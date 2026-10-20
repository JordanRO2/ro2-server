using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1589 (ReqReservedPCCount) by replying with a count of zero.</summary>
public class ReservedPCCountHandler : ZoneRmiHandlerBase<ReservedPCCountHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqReservedPCCount;
    public override string Name => "ReservedPCCount";

    public ReservedPCCountHandler(IServiceScopeFactory s, ILogger<ReservedPCCountHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        await session.SendRmiAsync(ZoneS2COpcodes.AnsReservedPCCount, writer =>
        {
            writer.WriteUInt32(0); 
        });
    }
}
