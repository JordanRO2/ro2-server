using System.Buffers.Binary;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Wire builders for the item family: 2503 OnUpdateItemDurability and 2326 OnItemInfo.
/// </summary>
/// <remarks>
/// <para>
/// Both messages are enveloped. A malformed envelope is rejected by the client, so the builders produce the
/// envelope and its self length themselves.
/// </para>
/// <para>
/// Offsets in the field comments are relative to the start of the blob that follows the compact length; the
/// first two blob bytes are the self length, which <see cref="PacketWriter.WriteCompactBABody"/> stamps.
/// </para>
/// </remarks>
public static class ItemPackets
{
    /// <summary>Value of a reserved dword that the client does not read.</summary>
    private const uint ReservedZero = 0;

    // ---- 2503 OnUpdateItemDurability ------------------------------------------------------------

    /// <summary>msgTag of 2503: 58317 (0xE3CD).</summary>
    internal const ushort MsgTagUpdateItemDurability = 58317;

    /// <summary>Body length of 2503: 1 marker byte + 1 length byte + 18 blob bytes.</summary>
    internal const int UpdateItemDurabilityBodyLen = 20;

    /// <summary>Full durability, as stored in the ItemInfo data table.</summary>
    public const ushort DurabilityMax = 10000;

    /// <summary>
    /// Builds RMI 2503 OnUpdateItemDurability: one worn item's new absolute durability.
    /// </summary>
    /// <param name="itemUid">The 64-bit item instance uid, as the item record carries it. Not the ItemInfo template id.</param>
    /// <param name="durability">The item's durability after the wear, absolute, from 0 to 10000. Not a delta.</param>
    /// <remarks>
    /// <para>
    /// Layout: <c>[u8 01][u8 0x12][u16 selfLen=18][u16 msgTag][u32 0][u64 itemUid][u16 durability]</c>.
    /// </para>
    /// <para>
    /// The message carries no actor handle, so it is sent only to the owner's own session.
    /// </para>
    /// </remarks>
    public static byte[] BuildItemDurability2503(ulong itemUid, ushort durability)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagUpdateItemDurability); // blob+2  msgTag
            p.WriteUInt32(ReservedZero);               // blob+4  reserved, not read by the client
            p.WriteUInt64(itemUid);                    // blob+8  item instance uid
            p.WriteUInt16(durability);                 // blob+16 absolute durability, 0..DurabilityMax
        });
        return pw.ToArray();
    }

    // ---- 2326 OnItemInfo -------------------------------------------------------------------------

    /// <summary>msgTag of 2326: 58064 (0xE2D0).</summary>
    internal const ushort MsgTagItemInfo = 58064;

    /// <summary>Body length of 2326: 3 envelope bytes (marker + u16 length) + 10 header bytes + the 126-byte record.</summary>
    internal const int ItemInfoBodyLen = 139;

    /// <summary>Length of the item record carried by 2326.</summary>
    public const int ItemRecordLength = 126;

    /// <summary>Item record offset of the u64 item instance uid.</summary>
    private const int RecordUidOffset = 0;

    /// <summary>Item record offset of the u32 ItemInfo template id.</summary>
    private const int RecordItemIdOffset = 8;

    /// <summary>Item record offset of the u16 durability.</summary>
    private const int RecordDurabilityOffset = 12;

    /// <summary>Result code 0 of 2326: the item is shown. Any other value makes the client show a result message instead.</summary>
    private const ushort ResultOk = 0;

    /// <summary>
    /// Builds RMI 2326 OnItemInfo, the answer to 1326 ReqItemDetailInfo, carrying one 126-byte item record.
    /// </summary>
    /// <param name="itemRecord126">
    /// The 126-byte item record, as the inventory list writes it. Must be exactly 126 bytes.
    /// </param>
    /// <param name="resultCode">Result code. Must be 0 for the item to be shown.</param>
    /// <remarks>
    /// <para>
    /// Layout: <c>[u8 02][u16 136][u16 selfLen=136][u16 msgTag][u32 0][u16 resultCode][126-byte item record]</c>.
    /// </para>
    /// <para>
    /// The message is sent only to the session that asked.
    /// </para>
    /// </remarks>
    public static byte[] BuildItemInfo2326(ReadOnlySpan<byte> itemRecord126, ushort resultCode = ResultOk)
    {
        if (itemRecord126.Length != ItemRecordLength)
        {
            throw new ArgumentException(
                $"2326 carries exactly {ItemRecordLength} bytes of item record; got {itemRecord126.Length}. " +
                "A short record makes the client read past the end of the blob.",
                nameof(itemRecord126));
        }

        // The lambda cannot close over a ReadOnlySpan<byte>, so copy once.
        var record = itemRecord126.ToArray();
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagItemInfo); // blob+2  msgTag
            p.WriteUInt32(ReservedZero);   // blob+4  reserved
            p.WriteUInt16(resultCode);     // blob+8  result code; non-zero aborts the client handler
            p.WriteBytes(record);          // blob+10 the item record
        });
        return pw.ToArray();
    }

    /// <summary>
    /// Builds RMI 2326 OnItemInfo from the three known record fields (uid, template id and durability), with the
    /// other 112 record bytes zero-filled.
    /// </summary>
    /// <remarks>
    /// Use this only where no complete item record is available. Zeroed fields such as sockets, enhancement and
    /// dye colours show as empty in the client's tooltip.
    /// </remarks>
    public static byte[] BuildItemInfo2326(ulong itemUid, uint itemId, ushort durability)
    {
        var record = new byte[ItemRecordLength];
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(RecordUidOffset), itemUid);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(RecordItemIdOffset), itemId);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(RecordDurabilityOffset), durability);
        return BuildItemInfo2326(record);
    }
}
