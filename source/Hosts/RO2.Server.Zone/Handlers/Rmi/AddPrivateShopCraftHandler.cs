using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.GameData;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1476 ReqAddPrivateShopCraft, which adds a craft recipe to the seller's private shop.
/// </summary>
/// <remarks>
/// The request is <c>[u16 craftId][u64 price]</c>. craftId is an entry of the Craft_ItemList data table.
/// The reply (2649 AnsAddPrivateShopCraft) is a u16 result of 0 followed by the 10 request bytes.
/// The offer is recorded in <see cref="PrivateShopRegistry"/> and stored per character, so it is restored
/// in later sessions. A request too short to hold both fields, or naming no recipe, is logged and not answered.
/// </remarks>
public class AddPrivateShopCraftHandler : ZoneRmiHandlerBase<AddPrivateShopCraftHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqAddPrivateShopCraft;
    public override string Name => "AddPrivateShopCraft";
    public AddPrivateShopCraftHandler(IServiceScopeFactory s, ILogger<AddPrivateShopCraftHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>Request length in bytes: u16 craftId and u64 price.</summary>
    private const int RequestBytes = 10;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < RequestBytes)
        {
            Logger.LogWarning("Session {Id}: AddPrivateShopCraft body is {Len} bytes, not the 10 retail sends",
                session.SessionId, payload.Length);
            return;
        }
        var span = payload.Span;
        ushort craftId = BinaryPrimitives.ReadUInt16LittleEndian(span);
        ulong price = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(2, 8));

        var store = services.GetRequiredService<GameDataStore>();
        var recipe = store.CraftItemList.FirstOrDefault(r => r.ID == craftId);
        if (recipe is null)
        {
            Logger.LogInformation("{Name} offered craft {CraftId}, which is not in Craft_ItemList; not answered",
                player.Name, craftId);
            return;
        }

        await PrivateShopCraftStore.SetAsync(player, craftId, price, services.GetRequiredService<IPrivateShopCraftRepository>(), ct);
        Logger.LogInformation("{Name} offers craft {CraftId} ('{Item}') at {Price} in private shop",
            player.Name, craftId, recipe.Craft_ItemName, price);

        await session.SendRmiAsync(ZoneS2COpcodes.AnsAddPrivateShopCraft, writer =>
        {
            writer.WriteUInt16(0);        
            writer.WriteUInt16(craftId);  
            writer.WriteUInt64(price);    
        });
    }
}
