using System.Buffers.Binary;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Quick-slot (skill and item bar) acknowledgements, which share one wire shape.
/// </summary>
/// <remarks>
/// The three acks are 12-byte compact-BA bodies that differ only in msgTag: 2473 OnAddQuickSlot, 2474 OnDelQuickSlot
/// and 2475 OnSwapQuickSlot. Each carries the actor handle and a zero, and no slot, type or item id, because the client
/// tracks its own bar state.
/// </remarks>
public static class QuickSlotPackets
{
    internal const ushort MsgTagAddQuickSlot = 58342;   
    internal const ushort MsgTagDelQuickSlot = 58340;   
    internal const ushort MsgTagSwapQuickSlot = 58338;  

 /// <summary>Body length of all three acks: 12 bytes, in every quick-slot ack.</summary>
    internal const int AckBodyLen = 12;

 /// <summary>Builds a quick-slot acknowledgement for the given msgTag.</summary>
    public static byte[] BuildAck(ushort msgTag, uint actorHandle)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(msgTag);
            p.WriteUInt32(actorHandle);
            p.WriteUInt16(0);
        });
        return pw.ToArray();
    }

 /// <summary>Reads the actor handle out of a quick-slot request.</summary>
 /// <remarks>
 /// The request is 30 bytes: [compactLen 01 1C][u16 innerSize 28][u16 msgTag][u32 actorHandle][fields...]. The handle
 /// is at blob+4. Inbound frames can carry transport bytes past the declared length, so the length is checked as a
 /// minimum rather than an exact match.
 /// </remarks>
    public static bool TryReadActorHandle(ReadOnlySpan<byte> body, out uint actorHandle)
        => TryReadRequest(body, out actorHandle, out _);

 /// <summary>A parsed quick-slot request.</summary>
 /// <param name="Page">Bar page, 0..2.</param>
 /// <param name="Slot">Slot within the page, 0..9.</param>
 /// <param name="EntryType">Entry kind, 1..3, stored verbatim rather than interpreted.</param>
 /// <param name="EntryId">Skill or item id.</param>
 /// <param name="Amount">Count or level; meaning not established.</param>
 /// <param name="ItemUid">Item uid, zero when the entry is not an item.</param>
    public readonly record struct QuickSlotRequest(
        byte Page, byte Slot, byte EntryType, uint EntryId, ushort Amount, ulong ItemUid);

 /// <summary>
 /// Parses a quick-slot request: the actor handle plus the bar position and its contents.
 /// </summary>
 /// <remarks>
 /// Short bodies (the del and swap variants) still yield the handle. The payload fields come back zeroed for them.
 /// </remarks>
    public static bool TryReadRequest(ReadOnlySpan<byte> body, out uint actorHandle,
        out QuickSlotRequest request)
    {
        actorHandle = 0;
        request = default;
        
        
        if (!body.TryGetCompactBlob(CompactLengthCheck.Lenient, out var blob) || blob.Length < 8)
            return false;

        actorHandle = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(4));

        if (blob.Length >= 28)
        {
            request = new QuickSlotRequest(
                Page: blob[8],
                Slot: blob[9],
                EntryType: blob[10],
                EntryId: BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(12)),
                Amount: BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(16)),
                ItemUid: BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(20)));
        }
        return true;
    }
}
