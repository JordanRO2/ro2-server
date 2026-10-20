using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Builders for the two trade notifications sent to the other party of a player-to-player trade:
/// 2383 OnBeginTradePCNotify and 2389 OnConfirmTradePCNotify.
/// </summary>
public static class TradeNotifyNotifyPackets
{
    
    
    
    private const ushort TagBeginTradePCNotify = 58037; 

 /// <summary>
 /// 2383 OnBeginTradePCNotify. Compact-BA body: [compactLen][u16 selfLen][u16 msgTag][payload].
 /// Payload: a reserved u32, two PC handles (initiator and target, in the order the client expects), and the result code at blob+8.
 /// </summary>
 /// <param name="resultCode">Result code at blob+8. 0 opens the trade window; a nonzero value shows a result message instead.</param>
    public static byte[] BuildOnBeginTradePCNotify(uint pcHandleA, uint pcHandleB, ushort resultCode = 0)
    {
        using var w = new PacketWriter(32);
        w.WriteCompactBABody(p =>
        {
            p.WriteUInt16(TagBeginTradePCNotify);
            p.WriteUInt32(0);          
            p.WriteUInt16(resultCode); 
            p.WriteUInt32(pcHandleA);  
            p.WriteUInt32(pcHandleB);  
        });
        return w.ToArray();
    }

 /// <summary>
 /// Result code for a rejected trade request. The initiator sees a rejection message.
 /// </summary>
    public const ushort RejectedResultCode = 17;

 /// <summary>
 /// Result code when an accept cannot open the window because a party is already in a live trade.
 /// </summary>
    public const ushort BusyResultCode = 14;

 /// <summary>
 /// Result code when an accept answers a request that is no longer the initiator's current one.
 /// </summary>
    public const ushort StaleRequestResultCode = 10;

 /// <summary>
 /// 2389 OnConfirmTradePCNotify. Blob: [u16 resultCode][u32 pcHandle][u64 money][u64 money][u16 itemCount][itemCount x 126-byte ItemRecord].
 /// Sent to both sides when a trade completes. Each item record must be exactly 126 bytes.
 /// </summary>
    public static byte[] BuildOnConfirmTradePCNotify(
        ushort resultCode,
        uint pcHandle,
        ulong money,
        IReadOnlyList<byte[]>? items = null)
    {
        using var blob = new PacketWriter(64);
        blob.WriteUInt16(resultCode); 
        blob.WriteUInt32(pcHandle);   
        blob.WriteUInt64(money);      
        blob.WriteUInt64(money);      
        int count = items?.Count ?? 0;
        blob.WriteUInt16((ushort)count); 
        if (items != null)
        {
            foreach (var it in items)
            {
                var rec = new byte[126]; 
                if (it is { Length: > 0 })
                    Array.Copy(it, rec, Math.Min(it.Length, 126));
                blob.WriteBytes(rec);
            }
        }

        var blobBytes = blob.ToArray();
        using var w = new PacketWriter(blobBytes.Length + 8);
        w.WriteUInt32((uint)blobBytes.Length); 
        w.WriteBytes(blobBytes);
        return w.ToArray();
    }
}
