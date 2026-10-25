using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1650 ReqGetSuperChanceRandomboxState (Empty) - Super randombox state</summary>
public class GetSuperChanceRandomboxStateHandler : ZoneRmiHandlerBase<GetSuperChanceRandomboxStateHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGetSuperChanceRandomboxState;
    public override string Name => "GetSuperChanceRandomboxState";

    public GetSuperChanceRandomboxStateHandler(IServiceScopeFactory s, ILogger<GetSuperChanceRandomboxStateHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        await session.SendRmiAsync(ZoneS2COpcodes.AnsGetSuperChanceRandomboxState, writer =>
        {
            writer.WriteUInt16(0); 
        });
    }
}
