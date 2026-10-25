using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Server.Zone.Models;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Builder for RMI 2848 NfyPCEquipView, the equipment appearance broadcast that lets nearby players see a character's worn gear.
/// </summary>
/// <remarks>
/// <para>
/// Compact-envelope message: <c>[compact length marker][u16 innerSize][u16 msgTag][u32 handle][u32 handle][u16 count][count x 28-byte record]</c>.
/// The actor handle is written twice. The first byte must be a valid compact marker (1, 2, 4 or 8); any other value makes the client's reader fail.
/// </para>
/// <para>
/// 2848 is a full snapshot, not a delta. Each broadcast must carry the complete worn set, because the client hides every slot
/// that is absent from the message. An empty body therefore means "wearing nothing" and must not be sent as a partial update.
/// </para>
/// <para>
/// Each record carries the item's appearance id, dye colours, element, refine level and FX enchant index. The costume overlay
/// field is not modelled and is always zero.
/// </para>
/// </remarks>
public static class EquipViewNotifyPackets
{
 /// <summary>msgTag of 2848 NfyPCEquipView.</summary>
    internal const ushort MsgTagEquipView = 58482;

 /// <summary>Size of one equipment record on the wire, in bytes.</summary>
    internal const int RecordSize = 28;

 /// <summary>
 /// Writes one 28-byte equipment record for a worn item into <paramref name="p"/>. This is the shared record definition used
 /// by the 2315 self record, the 2504 spawn record and the 2848 broadcast, so that the three stay in step.
 /// </summary>
 /// <remarks>
 /// <para>
 /// Layout: slot (u8, clamped to 0 to 26), item id (u32), dye colour 0 (u32), element (u8), refine level (u8), dye colour 1
 /// (u32), dye colour 2 (u32), a reserved zero (u32), FX enchant index (u32), and a trailing zero byte.
 /// </para>
 /// </remarks>
    public static void WriteEquipRecord28(PacketWriter p, InventoryItem it)
    {
        p.WriteByte((byte)Math.Clamp((int)it.Slot, 0, 26)); 
        p.WriteUInt32(it.ItemId);                           
        p.WriteUInt32(it.DyeColor);                         
        p.WriteByte(it.Element);                            
        p.WriteByte(it.Refine);                             
        p.WriteUInt32(it.DyeColor2);                        
        p.WriteUInt32(it.DyeColor3);                        
        p.WriteUInt32(0);                                   
        p.WriteUInt32(it.FxEnchantIndex);                   
        p.WriteByte(0);                                     
    }

 /// <summary>
 /// Builds the 28-byte equipment records for a worn-equipment snapshot. Only items in worn storage are included.
 /// </summary>
    public static List<byte[]> BuildEquipRecords28(IEnumerable<InventoryItem>? equipment)
    {
        var list = new List<byte[]>();
        if (equipment == null) return list;
        foreach (var it in equipment)
        {
            if (it.StorageType != 1) continue;
            using var pw = new PacketWriter(RecordSize);
            WriteEquipRecord28(pw, it);
            list.Add(pw.ToArray());
        }
        return list;
    }

 /// <summary>
 /// Reads <paramref name="player"/>'s full worn-equipment set and broadcasts the complete 2848 snapshot to every nearby
 /// player, so that observers see the current gear. Used by the equip, unequip, outfit, dye and costume handlers.
 /// </summary>
 /// <remarks>
 /// <para>
 /// If the database read fails, nothing is broadcast, rather than a wrong snapshot that would strip the player's gear for observers.
 /// </para>
 /// </remarks>
    public static async Task BroadcastFullSnapshotAsync(
        BroadcastService broadcast, IServiceProvider services, PlayerState player)
    {
        IReadOnlyList<InventoryItem> worn;
        try
        {
            var invRepo = services.GetRequiredService<IInventoryRepository>();
            worn = await invRepo.GetEquipmentAsync(player.CharacterId);
        }
        catch
        {
            
            return;
        }

        await BroadcastEquipChangeAsync(broadcast, player, worn, services.GetRequiredService<IGuildEmblemRepository>());
    }

 /// <summary>
 /// Broadcasts an equipment change of <paramref name="player"/> to nearby players: the full 2848 snapshot of
 /// <paramref name="worn"/>, followed by the player's 2315 view record built from the same worn set.
 /// </summary>
 /// <remarks>
 /// <para>
 /// The 2315 record is skipped when the player has no character snapshot to write it from. Both messages use the
 /// internal unencrypted transport.
 /// </para>
 /// </remarks>
    public static async Task BroadcastEquipChangeAsync(BroadcastService broadcast, PlayerState player,
        IReadOnlyList<InventoryItem> worn, IGuildEmblemRepository emblems)
    {
        var body = BuildEquipView2848(player.EntityId, BuildEquipRecords28(worn));
        await broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.NfyPCEquipView,
            w => w.WriteBytes(body));
        if (player.Character is { } character)
        {
            var viewData = ActorStatePackets.BuildOnPCViewData2315(player, character, worn, emblems);
            await broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.OnPCViewData,
                w => w.WriteBytes(viewData));
        }
    }

    public static byte[] BuildEquipView2848(uint pcHandle, IReadOnlyList<byte[]>? records28 = null)
    {
        int count = records28?.Count ?? 0;
        using var pw = new PacketWriter(32 + count * RecordSize);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagEquipView); 
            p.WriteUInt32(pcHandle);        
            p.WriteUInt32(pcHandle);        
            p.WriteUInt16((ushort)count);   
            if (records28 != null)
            {
                foreach (var rec in records28)
                {
                    if (rec.Length != RecordSize)
                        throw new ArgumentException(
                            $"NfyPCEquipView record must be exactly {RecordSize} bytes, got {rec.Length}");
                    p.WriteBytes(rec);
                }
            }
        });
        return pw.ToArray();
    }
}
