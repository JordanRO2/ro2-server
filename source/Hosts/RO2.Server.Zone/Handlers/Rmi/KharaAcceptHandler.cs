using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1433 ReqKharaAccept: accepts a Khara (achievement) mission.</summary>
public class KharaAcceptHandler : ZoneRmiHandlerBase<KharaAcceptHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqKharaAccept;
    public override string Name => "KharaAccept";
    public KharaAcceptHandler(IServiceScopeFactory s, ILogger<KharaAcceptHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        uint kharaId = 0;
        if (payload.Length >= 4)
        {
            var reader = new PacketReader(payload.Span);
            kharaId = reader.ReadUInt32();
        }
        Logger.LogInformation("{Name} accepts Khara mission {KharaId}", player.Name, kharaId);

        
        
        
        
        
        
        
        
        
        
        await session.SendRmiAsync(ZoneS2COpcodes.AnsKharaAccept, KharaService.BuildAnsKharaAccept2574(kharaId, 0), ct);

        
        
        
        
        
        
        
        
        await KharaService.OnAcceptedAsync(session, player, kharaId, services, Logger, ct);
    }
}
