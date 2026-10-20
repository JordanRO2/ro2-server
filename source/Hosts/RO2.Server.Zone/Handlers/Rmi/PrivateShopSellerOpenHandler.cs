using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1473 ReqOpenPrivateShopSellerUI: opens the seller's private shop setup window.</summary>
/// <remarks>
/// The request is empty. The answer (2646) lists the character's stored craft offers, which persist across sessions.
/// The setup window opens with no items.
/// </remarks>
public class PrivateShopSellerOpenHandler : ZoneRmiHandlerBase<PrivateShopSellerOpenHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqOpenPrivateShopSellerUI;
    public override string Name => "PrivateShopSellerOpen";
    public PrivateShopSellerOpenHandler(IServiceScopeFactory s, ILogger<PrivateShopSellerOpenHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>Size in bytes of the 2646 header before the craft entries: result, name and count.</summary>
    private const int HeaderBytes = 2 + PrivateShopRegistry.NameBytes + 2;

 /// <summary>Size in bytes of one 2646 craft entry: u16 craft id and u64 price.</summary>
    private const int CraftEntryBytes = 2 + 8;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        PrivateShopRegistry.BeginSetup(player);
        
        
        await PrivateShopCraftStore.EnsureLoadedAsync(player, services.GetRequiredService<IPrivateShopCraftRepository>(), ct);
        var crafts = PrivateShopRegistry.GetCrafts(player);
        Logger.LogDebug("{Name} opens private shop seller UI ({Count} craft offers)", player.Name, crafts.Count);

        await session.SendRmiAsync(ZoneS2COpcodes.AnsOpenPrivateShopSellerUI, BuildAnswer2646(crafts), ct);
    }

 /// <summary>
 /// Builds the 2646 body: compact length, result 0, empty name, craft count and the craft entries.
 /// </summary>
    internal static byte[] BuildAnswer2646(IReadOnlyList<PrivateShopCraftOffer> crafts)
    {
        using var writer = new PacketWriter(3 + HeaderBytes + CraftEntryBytes * crafts.Count);
        writer.WriteCompactLength(HeaderBytes + CraftEntryBytes * crafts.Count);
        writer.WriteUInt16(0);                         
        writer.WriteZeros(PrivateShopRegistry.NameBytes); 
        writer.WriteUInt16((ushort)crafts.Count);      
        foreach (var offer in crafts)
        {
            writer.WriteUInt16(offer.CraftId);         
            writer.WriteUInt64(offer.Price);           
        }
        return writer.ToArray();
    }
}
