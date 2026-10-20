using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1649 ReqGetChanceRandomboxPrizeInfo (Empty) - Randombox prize info</summary>
public class GetChanceRandomboxPrizeInfoHandler : ZoneRmiHandlerBase<GetChanceRandomboxPrizeInfoHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGetChanceRandomboxPrizeInfo;
    public override string Name => "GetChanceRandomboxPrizeInfo";

    public GetChanceRandomboxPrizeInfoHandler(IServiceScopeFactory s, ILogger<GetChanceRandomboxPrizeInfoHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        await session.SendRmiAsync(ZoneS2COpcodes.AnsGetChanceRandomboxPrizeInfo, writer =>
        {
            writer.WriteUInt16(0); 
        });
    }
}
