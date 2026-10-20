using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles ReqDestroyItem: removes an item from the character's inventory by its instance uid. The reply is OnDestroyItem.
/// </summary>
/// <remarks>
/// <para>
/// The item uid is read from the compact blob at offset 10. The item must belong to the requesting character; otherwise the request is ignored.
/// </para>
/// </remarks>
public class ItemDestroyHandler : ZoneRmiHandlerBase<ItemDestroyHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqDestroyItem;
    public override string Name => "ItemDestroy";
    public ItemDestroyHandler(IServiceScopeFactory s, ILogger<ItemDestroyHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 // Request blob layout: [u16 selfLen][u16 msgTag][u32 handle][u8 slot][u8 bag][u64 item UID].
    private const int ItemUidOffset = 10;
    private const int MinRequestBlobLength = ItemUidOffset + sizeof(ulong);

 // Client bag addressing: container 1 is the main bag (slots 50-69), containers 2-4 are the extra bags (slots 0-34);
 // worn equipment is also container 1, slots 0-49.
    private const byte StorageWorn = 1;
    private const byte MainBagContainer = 1;
    private const int MainBagFirstSlot = 50;
    private const int LastWornSlot = MainBagFirstSlot - 1;
    private const int FirstExtraBagContainer = 2;
    private const int LastExtraBagContainer = MainBagContainer + BagCapacity.ExtraBagCount;

 /// <summary>Offset of the count byte in the 126-byte item record. Count 0 empties the cell.</summary>
    private const int RecordCountOffset = 14;
    private const byte RecordCountEmpty = 0;

 // 2379 body layout: [msgTag][u32 actor handle @blob+4][u32 reserved @blob+8][126-byte item record @blob+12].
    private const uint DestroyReservedZero = 0;

 /// <summary>Initial buffer capacity for the 2379 body, which is 144 bytes.</summary>
    private const int DestroyBodyCapacity = 160;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        ulong itemUid;
        {
            
            if (!payload.Span.TryGetCompactBlob(CompactLengthCheck.Lenient, out var blob) || blob.Length < MinRequestBlobLength) return;
            itemUid = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(ItemUidOffset));
        }

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var it = await inventory.GetByIdAsync(itemUid);
        
        
        if (it is null || it.CharacterId != player.CharacterId) return;

        
        
        byte bagIx, slotIx;
        if (it.StorageType == StorageWorn)
        {
            bagIx = MainBagContainer;
            slotIx = (byte)Math.Clamp(it.Slot < 0 ? 0 : (int)it.Slot, 0, LastWornSlot);
        }
        else
        {
            var flat = it.Slot < 0 ? 0 : (int)it.Slot;
            if (flat < BagCapacity.MainBagCells) { bagIx = MainBagContainer; slotIx = (byte)(MainBagFirstSlot + flat); }
            else
            {
                var i = flat - BagCapacity.MainBagCells;
                bagIx = (byte)Math.Clamp(FirstExtraBagContainer + i / BagCapacity.ExtraBagStride, FirstExtraBagContainer, LastExtraBagContainer);
                slotIx = (byte)(i % BagCapacity.ExtraBagStride);
            }
        }

        
        var rec = WorldEntryService.BuildItemRecord126(it);
        rec[RecordCountOffset] = RecordCountEmpty;

        await inventory.RemoveItemAsync(itemUid);
        Logger.LogInformation("{Name} destroyed uid {Uid} (item {Item}) at bag {Bag}/slot {Slot}",
            player.Name, itemUid, it.ItemId, bagIx, slotIx);

        
        
        
        
        await KharaService.OnHeldItemChangedAsync(session, player, it.ItemId, services, Logger, ct);

        
        
        
        await QuestCollectDropTicks.OnCollectItemLeftBagAsync(session, player, it.ItemId, services, Logger, ct);

        
        
        
        
        
        using var pw = new PacketWriter(DestroyBodyCapacity);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(ShopItemPackets.MsgTagDestroyItem);  // msgTag 58055 (0xE2C7), RMI 2379
            p.WriteUInt32(player.EntityId);                    // blob+4  actor handle
            p.WriteUInt32(DestroyReservedZero);                // blob+8  reserved
            p.WriteBytes(rec);                                 // blob+12 item record, count 0
        });
        await session.SendRmiAsync(ZoneS2COpcodes.OnDestroyItem, w => w.WriteBytes(pw.ToArray()));
    }
}
