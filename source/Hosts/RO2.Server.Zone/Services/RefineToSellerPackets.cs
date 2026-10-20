using System.Text;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Wire builder for RMI 3005 OnRefineItemToSeller: the refine-shop owner's report of a refine that a buyer performed
/// in the shop.
/// </summary>
/// <remarks>
/// The body is <c>[01][u8 length][blob]</c>. The blob is [u16 0][u16 msgTag][u32 seller handle][u16 result]
/// [u32 outcome][u8 old grade][u8 new grade][u32 item template id][u8 0][WStr16 buyer name]. The result is always 0,
/// and a failed attempt is reported through the outcome. The client prints the item name, the buyer name and the grades.
/// </remarks>
internal static class RefineToSellerPackets
{
 /// <summary>3005 msgTag 58518 (0xE496).</summary>
    internal const ushort MsgTagRefineItemToSeller = 0xE496;

 /// <summary>Blob bytes before the name's characters: 8 + 2 + 4 + 1 + 1 + 4 + 1 + the u16 count.</summary>
    internal const int FixedBlobBytes = 23;

 /// <summary>
 /// Largest compact-length prefix: the width marker plus an 8-byte length. Added to the blob size for the writer's initial
 /// capacity.
 /// </summary>
    private const int MaxCompactPrefixBytes = 9;

 /// <summary>blob+0 inner-size field: written as 0 (not the blob length); the client does not read it.</summary>
    private const ushort InnerSizeUnused = 0;

 /// <summary>blob+8 result: 0 always. A non-zero value would make the client print a refine error code and stop; a failed
 /// attempt is reported through the outcome instead.</summary>
    private const ushort ResultOk = 0;

 /// <summary>blob+20 spare byte between the item template id and the buyer name; 0 and not read by the client.</summary>
    private const byte Spare = 0;

 /// <summary>
 /// Builds the 3005 body. The result is always 0, and a failed attempt is reported through <paramref name="outcome"/>.
 /// <paramref name="newGrade"/> is the grade shown after <paramref name="oldGrade"/>.
 /// </summary>
    internal static byte[] BuildRefineItemToSeller3005(uint sellerHandle, uint outcome, byte oldGrade, byte newGrade,
        uint itemTemplateId, string buyerName)
    {
        int chars = Math.Min(buyerName.Length, ushort.MaxValue);
        int blobBytes = FixedBlobBytes + 2 * chars;
        using var w = new PacketWriter(blobBytes + MaxCompactPrefixBytes);
        w.WriteCompactLength(blobBytes);
        w.WriteUInt16(InnerSizeUnused);             // blob+0  inner size: 0, not the blob length
        w.WriteUInt16(MsgTagRefineItemToSeller);    // blob+2  msgTag
        w.WriteUInt32(sellerHandle);                // blob+4  the receiver, the shop owner
        w.WriteUInt16(ResultOk);                    // blob+8  result
        w.WriteUInt32(outcome);                     // blob+10 0 success, 1 fail kept, 2 break, 3 downgrade
        w.WriteByte(oldGrade);                      // blob+14 grade before the attempt
        w.WriteByte(newGrade);                      // blob+15 grade printed after it
        w.WriteUInt32(itemTemplateId);              // blob+16 item template id of the refined item
        w.WriteByte(Spare);                         // blob+20
        w.WriteUInt16((ushort)chars);               // blob+21 WStr16: character count ...
        w.WriteBytes(Encoding.Unicode.GetBytes(buyerName, 0, chars)); // ... then UTF-16, no terminator
        return w.ToArray();
    }
}
