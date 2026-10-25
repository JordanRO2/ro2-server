using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1648 ReqSealItemClear. Unseals an item.</summary>
public class SealItemClearHandler : ZoneRmiHandlerBase<SealItemClearHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqSealItemClear;
    public override string Name => "SealItemClear";
    public SealItemClearHandler(IServiceScopeFactory s, ILogger<SealItemClearHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        if (payload.Length < 16) return;
        var reader = new PacketReader(payload.Span);
        reader.Skip(8); 

        var itemId = reader.ReadUInt32();
        var unsealScrollId = reader.ReadUInt32();

        Logger.LogDebug("{Name} unseals item {Id} with scroll {ScrollId}",
            player.Name, itemId, unsealScrollId);

        
        
    }
}
