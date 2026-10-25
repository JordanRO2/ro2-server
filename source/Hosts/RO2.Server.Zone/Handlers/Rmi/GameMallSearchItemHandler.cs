using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1602 ReqGameMallSearchItem (IDA: empty) - search cash shop items</summary>
public class GameMallSearchItemHandler : ZoneRmiHandlerBase<GameMallSearchItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGameMallSearchItem;
    public override string Name => "GameMallSearchItem";
    public GameMallSearchItemHandler(IServiceScopeFactory s, ILogger<GameMallSearchItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogDebug("{Name} searches game mall items", player.Name);

        
        await session.SendRmiAsync(2875, writer =>
        {
            writer.WriteUInt16(0); 
            writer.WriteUInt16(0); 
        });
    }
}
