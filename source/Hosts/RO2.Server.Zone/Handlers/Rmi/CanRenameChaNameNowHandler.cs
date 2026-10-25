using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1562 ReqCanRenameChaNameNow (Empty) - Check if rename is available</summary>
public class CanRenameChaNameNowHandler : ZoneRmiHandlerBase<CanRenameChaNameNowHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqCanRenameChaNameNow;
    public override string Name => "CanRenameChaNameNow";

    public CanRenameChaNameNowHandler(IServiceScopeFactory s, ILogger<CanRenameChaNameNowHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        await session.SendRmiAsync(ZoneS2COpcodes.AnsCanRenameChaNameNow, writer =>
        {
            writer.WriteUInt32(0); 
        });
    }
}
