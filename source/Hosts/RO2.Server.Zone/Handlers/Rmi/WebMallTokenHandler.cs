using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1453 (ReqWebMallToken): replies with an empty web mall token.</summary>
public class WebMallTokenHandler : ZoneRmiHandlerBase<WebMallTokenHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqWebMallToken;
    public override string Name => "WebMallToken";
    public WebMallTokenHandler(IServiceScopeFactory s, ILogger<WebMallTokenHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogDebug("{Name} requests web mall token", player.Name);
        
        await session.SendRmiAsync(ZoneS2COpcodes.AnsWebMallToken, writer =>
        {
            writer.WriteUInt16(0); 
            writer.WriteString(""); 
        });
    }
}
