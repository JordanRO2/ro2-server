using System;
using System.Collections.Generic;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Builders for the bank server-to-client packets. All bank item and gold movement is reported with the item-swap packet.
/// </summary>
public static class BankPackets
{
    /// <summary>Message tag of the bank item-swap packet (58104, 2344 OnBankItemSwap).</summary>
    public const ushort MsgTagBankItemSwap = 0xE2F8;

    /// <summary>Container id of the bank window. Inventory containers use ids 1 to 4.</summary>
    public const byte BankBag = 5;

    /// <summary>Length of one item record carried by the bank packets.</summary>
    private const int ItemRecordLength = ItemPackets.ItemRecordLength;

    /// <summary>
    /// Offset inside an item record of the container (bag) id. The item-swap packet copies this byte in front of
    /// each record as the window selector.
    /// </summary>
    private const int RecordContainerOffset = 0x0F;

    /// <summary>Result code of a successful bank packet.</summary>
    private const ushort ResultSuccess = 0;

    /// <summary>
    /// Bytes of the bank-data body that precede the records: owner handle (4), result (2), bank gold (8) and item
    /// count (2).
    /// </summary>
    private const int BankDataFixedLength = 16;

    /// <summary>Initial buffer capacity of the bank-data packet, excluding the records.</summary>
    private const int BankDataBaseCapacity = 32;

    /// <summary>Initial buffer capacity of the item-swap packet: the 264-byte blob plus the envelope.</summary>
    private const int ItemSwapCapacity = 272;

    /// <summary>
    /// Builds 2338 OnBankData: the full list of stored items and the bank gold.
    /// </summary>
    /// <remarks>
    /// Variable-length blob with a raw u32 length prefix. Body layout: [u32 innerLen = 16 + 126 * count][u32 ownerHandle][u16 result = 0][u64 bankGold][u16 count][count x 126-byte records]. An empty bank is a valid 23-byte frame.
    /// </remarks>
    /// <param name="ownerHandle">Handle of the bank owner.</param>
    /// <param name="bankGold">Gold stored in the bank.</param>
    /// <param name="records">126-byte item records.</param>
    public static byte[] BuildBankData2338(uint ownerHandle, long bankGold, IReadOnlyList<byte[]> records)
    {
        using var pw = new PacketWriter(BankDataBaseCapacity + ItemRecordLength * records.Count);
        var lenPos = pw.Reserve(4);
        var start = pw.Length;
        pw.WriteUInt32(ownerHandle);                            // body+0  owner handle
        pw.WriteUInt16(ResultSuccess);                          // body+4  result
        pw.WriteUInt64((ulong)Math.Max(0, bankGold));           // body+6  bank gold
        pw.WriteUInt16((ushort)records.Count);                  // body+14 item count
        foreach (var r in records) pw.WriteBytes(r);            // body+16 item records
        pw.WriteAt(lenPos, (uint)(pw.Length - start));          // innerLen = 16 + 126 * count
        return pw.ToArray();
    }

    /// <summary>
    /// Builds 2344 OnBankItemSwap: the acknowledgement of a move between the bank and the inventory. It applies both sides of the move.
    /// </summary>
    /// <remarks>
    /// Compact layout, 264 bytes: [u16 msgTag][u32 ownerHandle][u16 result = 0][u8 selA][recA 126][u8 selB][recB 126]. Each record is prefixed by a selector byte that picks the target window: 1 to 4 for inventory, 5 for the bank. recA is the source cell, cleared completely (template and count 0). recB is the moved item at its destination cell.
    /// </remarks>
    /// <param name="ownerHandle">Handle of the bank owner.</param>
    /// <param name="recA">Source record, cleared.</param>
    /// <param name="recB">Destination record, holding the moved item.</param>
    public static byte[] BuildBankItemSwap2344(uint ownerHandle, byte[] recA, byte[] recB)
    {
        using var pw = new PacketWriter(ItemSwapCapacity);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagBankItemSwap);          // blob+2   msgTag
            p.WriteUInt32(ownerHandle);                 // blob+4   owner handle
            p.WriteUInt16(ResultSuccess);               // blob+8   result
            p.WriteByte(recA[RecordContainerOffset]);   // blob+10  selector A: window of the source record
            p.WriteBytes(recA);                         // blob+11  source record, cleared
            p.WriteByte(recB[RecordContainerOffset]);   // blob+137 selector B: window of the destination record
            p.WriteBytes(recB);                         // blob+138 destination record
        });
        return pw.ToArray();
    }
}
