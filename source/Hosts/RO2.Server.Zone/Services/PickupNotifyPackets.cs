using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Wire builders for the public loot notification (2334, OnPickupItemNotify) and the corpse loot list
/// (2374, OnHeartBeatItem).
/// </summary>
public static class PickupNotifyPackets
{
 /// <summary>Message tag of 2334 (OnPickupItemNotify).</summary>
    internal const ushort MsgTagPickupItemNotify = 58020;

 /// <summary>Length of the 2334 blob, in bytes.</summary>
    internal const int BlobLen = 130;

 /// <summary>
 /// Builds RMI 2334 (OnPickupItemNotify): the broadcast that tells nearby players someone looted an item.
 /// </summary>
 /// <param name="pickerHandle">Actor handle of the player who picked up the item.</param>
 /// <param name="lootedItemUid">Item instance UID.</param>
 /// <param name="itemTemplateId">Template ID of the item.</param>
    public static byte[] BuildPickupItemNotify2334(uint pickerHandle, ulong lootedItemUid, uint itemTemplateId)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagPickupItemNotify); 
            p.WriteUInt32(0);                      
            p.WriteUInt32(pickerHandle);           
            p.WriteUInt64(lootedItemUid);          
            p.WriteUInt32(itemTemplateId);         

            
            
            int payloadTarget = BlobLen - 2;       
            int pad = payloadTarget - p.GetWrittenSpan().Length;
            if (pad > 0) p.WriteBytes(new byte[pad]);
        });
        return pw.ToArray();
    }

    
 /// <summary>Message tag of 2374 (OnHeartBeatItem).</summary>
    internal const ushort MsgTagHeartBeatItem = 58030;

 /// <summary>Length of one item entry in a 2374 body, in bytes.</summary>
    internal const int HeartBeatItemEntryLen = 30;

 /// <summary>Length of one looter entry in a 2374 body, in bytes.</summary>
    internal const int HeartBeatLooterEntryLen = 9;

 /// <summary>
 /// Builds RMI 2374 (OnHeartBeatItem): the answer to a 1338 request on a corpse that still holds loot for the
 /// requester. Lists the corpse's items and who may loot them.
 /// </summary>
    public static byte[] BuildHeartBeatItem2374(uint corpseHandle, IReadOnlyList<GroundItemService.CorpseItem> items,
        IReadOnlyList<ulong> looterGids)
    {
        if (items.Count is 0 or > byte.MaxValue)
            throw new ArgumentException($"2374 lists 1..255 items; got {items.Count}.", nameof(items));
        if (looterGids.Count is 0 or > byte.MaxValue)
            throw new ArgumentException($"2374 lists 1..255 looters; got {looterGids.Count}.", nameof(looterGids));

        using var pw = new PacketWriter(20 + HeartBeatItemEntryLen * items.Count + HeartBeatLooterEntryLen * looterGids.Count);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagHeartBeatItem);   
            p.WriteUInt32(0);                     
            p.WriteUInt32(corpseHandle);          
            p.WriteByte(0);                       
            p.WriteByte((byte)items.Count);       
            p.WriteByte((byte)looterGids.Count);  
            foreach (var it in items)
            {
                p.WriteUInt32(it.ItemTemplateId);                             
                p.WriteUInt64(it.Uid);                                        
                p.WriteByte((byte)Math.Clamp(it.Count, 1, byte.MaxValue));    
                p.WriteBytes(new byte[HeartBeatItemEntryLen - 13]);           
            }
            foreach (var gid in looterGids)
            {
                p.WriteByte(1);                   
                p.WriteUInt64(gid);               
            }
        });
        return pw.ToArray();
    }
}
