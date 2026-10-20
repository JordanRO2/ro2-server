using System;
using System.Collections.Generic;
using System.Linq;
using RO2.Domain.Entities;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Incubator slot list (2926, AnsPCollectionIncubator) built from the character's incubator slots.
/// </summary>
/// <remarks>
/// <para>
/// Body: a compact length <c>n</c> (the record count), then <c>n</c> records of 58 bytes, then a u16 result.
/// The result is 0 for every answer; a non-zero result would make the client ignore the frame.
/// </para>
/// <para>
/// Record: u32 incubator ID at @0, u32 egg item ID at @4, u8 slot at @8 (0 or 1), u8 charges left at @9,
/// an OLE date start at @10, an OLE date end at @26 and an OLE span in days at @42. Each date and span is
/// followed by a status word and padding.
/// </para>
/// </remarks>
public static class PetIncubatorPackets
{
 /// <summary>Length of one 2926 record, in bytes.</summary>
    public const int RecordLength = 58;

 /// <summary>
 /// Builds a 2926 answer from the incubator slots, in slot order. With no slots, the body is the empty list.
 /// </summary>
    public static byte[] BuildAnsPCollectionIncubator(IReadOnlyList<PetIncubatorSlot> slots, DateTime nowUtc)
    {
        var ordered = slots.OrderBy(s => s.Slot).ToList();
        using var w = new PacketWriter(4 + RecordLength * ordered.Count);
        w.WriteCompactLength(ordered.Count);
        foreach (var s in ordered)
            WriteRecord(w, s, nowUtc);
        w.WriteUInt16(0);   
        return w.ToArray();
    }

    private static void WriteRecord(PacketWriter w, PetIncubatorSlot s, DateTime nowUtc)
    {
        w.WriteUInt32(s.IncubatorId);                       
        w.WriteUInt32(s.EggId);                             
        w.WriteByte(s.Slot);                                
        w.WriteByte(s.Charges);                             
        WriteDateTime(w, OleDate(s.StartUtc));              
        WriteDateTime(w, OleDate(s.EndUtc));                
        WriteDateTime(w, (AsUtc(s.EndUtc) - AsUtc(nowUtc)).TotalDays); 
    }

 /// <summary>Writes an MFC COleDateTime value: f64, u32 status and u32 padding.</summary>
    private static void WriteDateTime(PacketWriter w, double value)
    {
        w.WriteDouble(value);
        w.WriteUInt32(0);   
        w.WriteUInt32(0);   
    }

 /// <summary>The OLE automation date of a stored UTC instant, in server local time.</summary>
    internal static double OleDate(DateTime utc) => AsUtc(utc).ToLocalTime().ToOADate();

    
    private static DateTime AsUtc(DateTime t) => t.Kind == DateTimeKind.Utc ? t : DateTime.SpecifyKind(t, DateTimeKind.Utc);
}
