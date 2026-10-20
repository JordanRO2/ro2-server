using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Server-to-client builders for the trade-window opcodes. Each uses the compact-length ByteArray envelope
/// [compactLen][u16 selfLen][u16 msgTag][payload], which keeps the length marker valid so the client decodes the body.
/// </summary>
public static class TradePackets
{
    
    private const ushort TagOpenTradePC = 58034;  
    private const ushort TagDragTradePC = 58039;  
    private const ushort TagDelTradePC = 58041;   
    private const ushort TagBeginTradePC = 58035; 
    private const ushort TagDragGold = 58043;      
    private const ushort TagLockTradePC = 58045;   
    private const ushort TagConfirmTradePC = 58047; 

 /// <summary>
 /// 2382 OnBeginTradePC: the trade request prompt, sent only to the trade target.
 /// The handle is the initiator's, which the client resolves to the initiator's name.
 /// Payload: [u16 msgTag][u32 0][u32 pcHandle].
 /// </summary>
 /// <param name="requesterHandle">The initiator's PC actor handle.</param>
    public static byte[] BuildOnBeginTradePC(uint requesterHandle)
    {
        using var w = new PacketWriter(32);
        w.WriteCompactBABody(p =>
        {
            p.WriteUInt16(TagBeginTradePC);
            p.WriteUInt32(0);               
            p.WriteUInt32(requesterHandle); 
        });
        return w.ToArray();
    }

 /// <summary>
 /// 2388 OnConfirmTradePC: the per-confirm echo, sent to both sides. Payload: [u16 msgTag][u32 0][u32 pcHandle][u8 result].
 /// The client closes the trade window on any result other than 1. Each recipient gets its own build, because the handle depends on the result.
 /// </summary>
    public static byte[] BuildOnConfirmTradePC(uint confirmerHandle, byte result)
    {
        using var w = new PacketWriter(32);
        w.WriteCompactBABody(p =>
        {
            p.WriteUInt16(TagConfirmTradePC);
            p.WriteUInt32(0);                
            p.WriteUInt32(confirmerHandle);  
            p.WriteByte(result);             
        });
        return w.ToArray();
    }

 /// <summary>
 /// 2386 OnDragGold: the gold offer shown in the window. Payload: [u16 msgTag][u32 0][i16 result][u32 pcHandle][u64 gold].
 /// The amount is a 64-bit value.
 /// </summary>
    public static byte[] BuildOnDragGold(uint pcHandle, ulong gold)
    {
        using var w = new PacketWriter(40);
        w.WriteCompactBABody(p =>
        {
            p.WriteUInt16(TagDragGold);
            p.WriteUInt32(0);        
            p.WriteUInt16(0);        
            p.WriteUInt32(pcHandle); 
            p.WriteUInt64(gold);     
        });
        return w.ToArray();
    }

 /// <summary>
 /// 2387 OnLockTradePC: the lock state for a lock round. The first message of a round has phase 0; the second has phase 1,
 /// which raises the final confirm dialog. Payload: [u16 msgTag][u32 0][u8 phase][u32 pcHandle][u8 locked].
 /// </summary>
 /// <param name="bothLocked">Phase selector: 0 for the first lock of a round, 1 for the second.</param>
 /// <param name="locked">Lock state of the side named by the handle. Defaults to true.</param>
    public static byte[] BuildOnLockTradePC(uint pcHandle, bool bothLocked, bool locked = true)
    {
        using var w = new PacketWriter(32);
        w.WriteCompactBABody(p =>
        {
            p.WriteUInt16(TagLockTradePC);
            p.WriteUInt32(0);                        
            p.WriteByte((byte)(bothLocked ? 1 : 0)); 
            p.WriteUInt32(pcHandle);                 
            p.WriteByte((byte)(locked ? 1 : 0));     
        });
        return w.ToArray();
    }

 /// <summary>
 /// 2381 OnOpenTradePC: the initiator's answer to its own 1351 request. Payload: [u16 msgTag][u32 0][i16 result][u32 target].
 /// A result of 0 means no refusal; other codes show a message.
 /// </summary>
    public static byte[] BuildOnOpenTradePC(uint targetHandle, short resultCode)
    {
        using var w = new PacketWriter(32);
        w.WriteCompactBABody(p =>
        {
            p.WriteUInt16(TagOpenTradePC);
            p.WriteUInt32(0);           
            p.WriteInt16(resultCode);   
            p.WriteUInt32(targetHandle); 
        });
        return w.ToArray();
    }

 /// <summary>
 /// 2381 result 14: the initiator is already in a live trade.
 /// </summary>
    public const short OpenTradeRefusedBusy = 14;

 /// <summary>
 /// 2381 result 10: the client shows no message. Sent to a target that has recently been busy with another activity.
 /// </summary>
    public const short OpenTradeRefusedUnavailable = 10;

 /// <summary>
 /// 2384 OnDragTradePC: the response to an item drag. Payload: [u16 msgTag][6 bytes reserved][u32 pcHandle][126-byte ItemRecord].
 /// The i16 result at blob+8 selects a message; a nonzero result leaves the item out of the window.
 /// </summary>
    public static byte[] BuildOnDragTradePC(uint pcHandle, ReadOnlySpan<byte> itemRecord126, short resultCode)
        => BuildTradeItemDrag(TagDragTradePC, pcHandle, itemRecord126, resultCode);

 /// <summary>Zero-item overload, for an empty slot.</summary>
    public static byte[] BuildOnDragTradePC(uint pcHandle)
        => BuildTradeItemDrag(TagDragTradePC, pcHandle, ReadOnlySpan<byte>.Empty, 0);

 /// <summary>
 /// 2384 result 1: the drag was not added, because the uid is already in the window or the side is full.
 /// The record carries the requested count.
 /// </summary>
    public const short DragRefused = 1;

 /// <summary>
 /// 2384 result 20063: the item cannot be traded. The record is built from the request, not from the item.
 /// </summary>
    public const short DragItemNotTradeable = 20063;

 /// <summary>
 /// The 126-byte record of a 2384 <see cref="DragItemNotTradeable"/> answer: the uid at +0, the template id at +8,
 /// the count at +14, and zeros elsewhere.
 /// </summary>
    public static byte[] BuildRefusedDragRecord(ulong uid, uint templateId, int count)
    {
        var rec = new byte[126];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(rec, uid);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(8), templateId);
        rec[14] = (byte)Math.Clamp(count, 0, 255);
        return rec;
    }

 /// <summary>
 /// 2385 OnDelTradePC: the item removed from the trade window. Same shape as 2384, with the item record identifying the removed item.
 /// </summary>
    public static byte[] BuildOnDelTradePC(uint pcHandle, ReadOnlySpan<byte> itemRecord126)
        => BuildTradeItemDrag(TagDelTradePC, pcHandle, itemRecord126, 0);

 /// <summary>Zero-item overload.</summary>
    public static byte[] BuildOnDelTradePC(uint pcHandle)
        => BuildTradeItemDrag(TagDelTradePC, pcHandle, ReadOnlySpan<byte>.Empty, 0);

 /// <summary>
 /// Shared writer for the 2384 and 2385 item-drag messages: [u16 msgTag][u32 0][i16 result][u32 pcHandle][126-byte ItemRecord].
 /// The record is always emitted at exactly 126 bytes, padded or truncated.
 /// </summary>
    private static byte[] BuildTradeItemDrag(ushort msgTag, uint pcHandle, ReadOnlySpan<byte> itemRecord126, short resultCode)
    {
        var rec = new byte[126]; 
        if (itemRecord126.Length > 0)
            itemRecord126.Slice(0, Math.Min(itemRecord126.Length, 126)).CopyTo(rec);
        using var w = new PacketWriter(160);
        w.WriteCompactBABody(p =>
        {
            p.WriteUInt16(msgTag);
            p.WriteUInt32(0);          
            p.WriteInt16(resultCode);  
            p.WriteUInt32(pcHandle);   
            p.WriteBytes(rec);         
        });
        return w.ToArray();
    }
}
