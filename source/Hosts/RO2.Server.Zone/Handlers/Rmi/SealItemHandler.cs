using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1647 ReqSealItem. Seals an item, binding it to the character.</summary>
public class SealItemHandler : ZoneRmiHandlerBase<SealItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqSealItem;
    public override string Name => "SealItem";
    public SealItemHandler(IServiceScopeFactory s, ILogger<SealItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        if (payload.Length < 16) return;
        var reader = new PacketReader(payload.Span);
        reader.Skip(8); 

        var itemId = reader.ReadUInt32();
        var sealScrollId = reader.ReadUInt32();

        var store = services.GetRequiredService<GameDataStore>();
        var item = store.GetItem(itemId);

        Logger.LogDebug("{Name} seals item '{Item}' with scroll {ScrollId}",
            player.Name, item?.Name ?? "?", sealScrollId);

        
        
    }
}
