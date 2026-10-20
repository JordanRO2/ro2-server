using System.Collections.Generic;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// One card-book record as carried by the card status update (2589): a card id, a bind flag, the copy count, the enforce grade and a trailing flag.
/// </summary>
/// <param name="CardId">Card id, equal to the card item's template id.</param>
/// <param name="B4">Constant flag byte of the record.</param>
/// <param name="Count">Copies of that card still in the book after the change.</param>
/// <param name="Grade">The enforce grade named by the request.</param>
/// <param name="B7">Constant trailing byte of the record.</param>
public readonly record struct CardStatusRecord(uint CardId, byte B4, byte Count, byte Grade, byte B7);

/// <summary>
/// Builds the card status update (RMI 2589, <c>AnsUpdateCardStatus</c>). The payload uses a 32-bit length-prefixed blob: a length, a header, a record count, then the 8-byte records.
/// </summary>
/// <remarks>
/// The length field equals <c>4 + 8 * count</c>. A successful card book take-off or put is answered with a single record; the update request is answered with an empty record list.
/// </remarks>
public static class CardStatusPackets
{
    public static byte[] BuildUpdateCardStatus2589(IReadOnlyList<CardStatusRecord> records)
    {
        using var pw = new PacketWriter(8 + 8 * records.Count);
        var lenPos = pw.Reserve(4);
        var start = pw.Length;
        pw.WriteUInt16(0);                          
        pw.WriteUInt16((ushort)records.Count);      
        foreach (var r in records)
        {
            pw.WriteUInt32(r.CardId);
            pw.WriteByte(r.B4);
            pw.WriteByte(r.Count);
            pw.WriteByte(r.Grade);
            pw.WriteByte(r.B7);
        }
        pw.WriteAt(lenPos, (uint)(pw.Length - start));
        return pw.ToArray();
    }
}
