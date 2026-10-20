using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Wire builders for the loot and item-use notifications: 2333 OnPickupItem and 2330 OnUseItemNotify.
/// </summary>
/// <remarks>
/// 2330 uses the compact-length byte array envelope. 2333 is a bare body with no envelope and no message tag, so it must
/// not be given a compact-length marker.
/// </remarks>
public static class LootNotifyPackets
{
    
 /// <summary>Body length of a 2333 message.</summary>
    internal const int PickupItemBodyLen = 130;

 /// <summary>Length of the item record carried by a 2333 message.</summary>
    public const int ItemRecordLength = 126;

 /// <summary>
 /// Builds the 2333 OnPickupItem message, sent only to the picker, carrying the full item record of the picked-up item.
 /// </summary>
 /// <param name="itemRecord126">
 /// The 126-byte item record, as written by <c>WorldEntryService.BuildItemRecord126(InventoryItem)</c>.
 /// </param>
 /// <param name="resultCode">Result code. Must be 0 for the pickup to be accepted. A non-zero value is shown as an error.</param>
 /// <param name="pickupKind">Message selector for the pickup text. 0 is an ordinary pickup.</param>
 /// <remarks>
 /// Layout: a u16 result code, a u16 pickup kind, then the 126-byte item record. The record's uid is not an echo of the
 /// pickup request, so the record of the item actually granted must be sent. The message is unicast to the picker and never
 /// broadcast, because it carries private inventory data. Use the picker's own session.
 /// </remarks>
    public static byte[] BuildPickupItem2333(ReadOnlySpan<byte> itemRecord126,
        ushort resultCode = 0, ushort pickupKind = 0)
    {
        if (itemRecord126.Length != ItemRecordLength)
        {
            throw new ArgumentException(
                $"2333 carries exactly {ItemRecordLength} bytes of item record; got {itemRecord126.Length}. " +
                "A short record makes the client read past the end of the 130-byte blob.",
                nameof(itemRecord126));
        }

        using var pw = new PacketWriter(PickupItemBodyLen);
        pw.WriteUInt16(resultCode); 
        pw.WriteUInt16(pickupKind); 
        pw.WriteBytes(itemRecord126); 
        return pw.ToArray();
    }

 /// <summary>
 /// Builds the 2333 OnPickupItem message from the three known record fields, with the other record bytes zeroed.
 /// </summary>
 /// <remarks>
 /// Use this only when no real inventory record is available. Sockets, enhancement and dye read as empty.
 /// </remarks>
    public static byte[] BuildPickupItem2333(ulong itemUid, uint itemId, ushort durability,
        ushort resultCode = 0, ushort pickupKind = 0)
    {
        var record = new byte[ItemRecordLength];
        using (var rw = new PacketWriter(ItemRecordLength))
        {
            rw.WriteUInt64(itemUid);    
            rw.WriteUInt32(itemId);     
            rw.WriteUInt16(durability); 
            rw.GetWrittenSpan().CopyTo(record);
        }
        return BuildPickupItem2333(record, resultCode, pickupKind);
    }

    
 /// <summary>Message tag of the 2330 notification.</summary>
    internal const ushort MsgTagUseItemNotify = 58023;

 /// <summary>Body length of a 2330 message.</summary>
    internal const int UseItemNotifyBodyLen = 22;

 /// <summary>
 /// Builds the 2330 OnUseItemNotify message, which tells nearby players that an actor used an item so they can play the
 /// use animation. Paired with 2329 OnUseItem, which updates the user's bag.
 /// </summary>
 /// <param name="actorHandle">Actor handle of the player who used the item. Written twice, as the self handle.</param>
 /// <param name="itemTemplateId">Item template id of the consumed item, not the instance id.</param>
 /// <remarks>
 /// Layout: a compact-length envelope, a u16 self length, the message tag, a zero u32, the actor handle, the item template id,
 /// and the actor handle again. The message is broadcast to nearby players including the source. It must not be sent
 /// through a broadcast that excludes the source.
 /// </remarks>
    public static byte[] BuildUseItemNotify2330(uint actorHandle, uint itemTemplateId)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagUseItemNotify); 
            p.WriteUInt32(0);                   
            p.WriteUInt32(actorHandle);         
            p.WriteUInt32(itemTemplateId);      
            p.WriteUInt32(actorHandle);         
        });
        return pw.ToArray();
    }
}
