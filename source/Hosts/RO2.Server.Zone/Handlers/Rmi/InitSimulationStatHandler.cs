using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1536 ReqInitSimulationStat (empty body): replies with a zero result code and resets nothing.
/// </summary>
public class InitSimulationStatHandler : ZoneRmiHandlerBase<InitSimulationStatHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqInitSimulationStat;
    public override string Name => "InitSimulationStat";

    public InitSimulationStatHandler(IServiceScopeFactory s, ILogger<InitSimulationStatHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        await session.SendRmiAsync(ZoneS2COpcodes.AnsInitSimulationStat, writer =>
        {
            writer.WriteUInt16(0); 
        });
    }
}
