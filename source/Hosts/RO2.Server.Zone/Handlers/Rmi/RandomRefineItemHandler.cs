using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1676 (ReqRandomRefineItem): attempts a random refine of an equipment item using a material.</summary>
/// <remarks>
/// Wire layout of the 50-byte blob:
/// <code>
/// blob+0 u16 innerSize = 50
/// blob+2 u16 msgTag
/// blob+4 u32 actor handle
/// blob+17 item descriptor, 11 bytes: [u8 slot][u8 bag][u8 count][u64 uid] (target item)
/// blob+28 item descriptor, 11 bytes (material)
/// blob+39 item descriptor, 11 bytes (unused)
/// </code>
/// The target item is refined and the material is consumed. The result is answered with RMI 3009 and
/// carries the item record of the target.
/// </remarks>
public class RandomRefineItemHandler : ZoneRmiHandlerBase<RandomRefineItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqRandomRefineItem;
    public override string Name => "RandomRefineItem";

 /// <summary>Expected length of the request body.</summary>
    private const int BlobLength = 50;

 /// <summary>Offset of the target item descriptor within the blob.</summary>
    private const int TargetDescriptor = 17;

 /// <summary>Offset of the material item descriptor within the blob.</summary>
    private const int MaterialDescriptor = 28;

    public RandomRefineItemHandler(IServiceScopeFactory s, ILogger<RandomRefineItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var body = payload.Span;
        
        if (!body.TryGetCompactBlob(CompactLengthCheck.Strict, out var blob) || blob.Length < BlobLength)
        {
            Logger.LogWarning("Session {Id}: RandomRefineItem body is {Len} bytes, not the 52-byte compact frame retail sends",
                session.SessionId, body.Length);
            return;
        }

        byte targetSlot = blob[TargetDescriptor];
        byte targetBag = blob[TargetDescriptor + 1];
        byte targetCount = blob[TargetDescriptor + 2];
        ulong targetUid = BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(TargetDescriptor + 3, 8));
        ulong materialUid = BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(MaterialDescriptor + 3, 8));

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var store = services.GetRequiredService<GameDataStore>();
        var target = await inventory.GetByIdAsync(targetUid);
        bool owned = target is not null && target.CharacterId == player.CharacterId;

        Logger.LogDebug("{Name} random-refines uid {Uid} ('{Item}', bag {Bag} slot {Slot}, owned={Owned}) with material uid {Mat}",
            player.Name, targetUid, owned ? store.GetItem(target!.ItemId)?.Name ?? "?" : "?",
            targetBag, targetSlot, owned, materialUid);

        
        
        
        
        
        
        
        var material = materialUid != 0 ? await inventory.GetByIdAsync(materialUid) : null;
        uint materialItem = material?.ItemId ?? 0;
        bool materialUsable = material is not null && material.CharacterId == player.CharacterId
            && material.StorageType == 0 && material.Count > 0
            && store.GrindingItem.Any(g => g.Item_ID == materialItem);
        if (!materialUsable)
        {
            var noMaterial = ShopItemPackets.BuildRandomRefineItem3009(player.EntityId, ResultNoMaterial,
                DescriptorEcho(targetUid, targetCount, targetBag, targetSlot));
            await session.SendRmiAsync(ZoneS2COpcodes.OnRandomRefineItem, writer => writer.WriteBytes(noMaterial));
            return;
        }

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        var record = owned ? WorldEntryService.BuildItemRecord126(target!) : DescriptorEcho(targetUid, targetCount, targetBag, targetSlot);
        var randomRefineBody = ShopItemPackets.BuildRandomRefineItem3009(player.EntityId, ResultCannotReroll, record);
        await session.SendRmiAsync(ZoneS2COpcodes.OnRandomRefineItem,
            writer => writer.WriteBytes(randomRefineBody));
    }

 /// <summary>Result code 6: the re-roll was refused. Nothing is charged or consumed.</summary>
    private const ushort ResultCannotReroll = 6;

 /// <summary>Result code 3: the request has no usable material. Used when the material descriptor is empty.</summary>
    internal const ushort ResultNoMaterial = 3;

 /// <summary>
 /// Builds a minimal item record that carries only the request's descriptor (uid, count, bag and slot)
 /// with template 0. Used for the result-3 answer.
 /// </summary>
    internal static byte[] DescriptorEcho(ulong uid, byte count, byte bag, byte slot)
    {
        var echo = new byte[ItemPackets.ItemRecordLength];
        BinaryPrimitives.WriteUInt64LittleEndian(echo, uid);
        echo[14] = count;
        echo[15] = bag;
        echo[16] = slot;
        return echo;
    }
}
