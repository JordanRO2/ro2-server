using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Wire builder for RMI 2362 (OnPartyNotifyGetItem), the party loot-log broadcast.
/// </summary>
/// <remarks>
/// The body uses the compact-length envelope (<see cref="PacketWriter.WriteCompactBABody"/>), which the
/// client requires; a raw body is rejected by the client's decoder.
/// </remarks>
public static class PartyLootNotifyPackets
{
 /// <summary>Message tag of RMI 2362.</summary>
    public const ushort MsgTagPartyNotifyGetItem = 58166; 

 /// <summary>Body length of RMI 2362, in bytes.</summary>
    public const int BodyLen = 35;

 /// <summary>
 /// Builds RMI 2362 (OnPartyNotifyGetItem) for a looted item.
 /// </summary>
 /// <param name="receiverGid">Character GID of the party member who received the item.</param>
 /// <param name="itemUid">Item instance UID.</param>
 /// <param name="sourceNpcHandle">Actor handle of the monster that dropped the item.</param>
 /// <param name="itemTemplateId">Template ID of the looted item.</param>
 /// <param name="trailing">Items still on the corpse after this one. Zero removes the corpse from the receivers' loot lists.</param>
    public static byte[] BuildPartyNotifyGetItem2362(
        ulong receiverGid, ulong itemUid, uint sourceNpcHandle, uint itemTemplateId, byte trailing = 0)
    {
        using var pw = new PacketWriter(BodyLen);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagPartyNotifyGetItem); 
            p.WriteUInt32(0);                        
            p.WriteUInt64(receiverGid);              
            p.WriteUInt64(itemUid);                  
            p.WriteUInt32(sourceNpcHandle);          
            p.WriteUInt32(itemTemplateId);           
            p.WriteByte(trailing);                   
        });
        return pw.ToArray();
    }
}
