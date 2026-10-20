using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>RMI 2512 OnFuncObject_In: lists the function objects that enter the recipient's world.</summary>
/// <remarks>
/// The body is a compact frame of 42-byte records. Each record holds the handle, the template id, the position,
/// the yaw and a flag byte. The client keeps a record only when its template id is known to it.
/// </remarks>
internal static class FuncObjectPackets
{
 /// <summary>Message tag of the 2512 body.</summary>
    internal const ushort MsgTagFuncObjectIn = 58350;

 /// <summary>Bytes of one FuncObj42 record.</summary>
    internal const int RecordLength = 42;

 /// <summary>The most records in one 2512 body.</summary>
    internal const int MaxRecordsPerFrame = 87;

 /// <summary>Builds one 2512 body listing <paramref name="objects"/>. An empty list gives a count-0 body.</summary>
    internal static byte[] BuildFuncObjectIn2512(IReadOnlyList<FuncObject> objects)
    {
        if (objects.Count > MaxRecordsPerFrame)
            throw new ArgumentOutOfRangeException(nameof(objects), objects.Count, "at most 87 records per 2512");
        using var pw = new PacketWriter(16 + RecordLength * objects.Count);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagFuncObjectIn);   
            p.WriteUInt32(0);                    
            p.WriteUInt16((ushort)objects.Count); 
            foreach (var o in objects)
            {
                p.WriteUInt32(o.Handle);         
                p.WriteUInt32(o.TemplateId);     
                p.WriteSingle(o.X);              
                p.WriteSingle(o.Y);              
                p.WriteSingle(o.Z);              
                p.WriteByte(1);                  
                p.WriteSingle(o.Rotation);       
                p.WriteZeros(16);                
                p.WriteByte(0);                  
            }
        });
        return pw.ToArray();
    }

 /// <summary>
 /// Builds the 2512 frames for a map load: <paramref name="objects"/> split into frames of at most
 /// <see cref="MaxRecordsPerFrame"/> records, or one count-0 frame when the map has none.
 /// </summary>
    internal static IReadOnlyList<byte[]> BuildMapLoadFrames2512(IReadOnlyList<FuncObject> objects)
    {
        if (objects.Count == 0) return new[] { BuildFuncObjectIn2512(objects) };
        var frames = new List<byte[]>((objects.Count + MaxRecordsPerFrame - 1) / MaxRecordsPerFrame);
        for (int i = 0; i < objects.Count; i += MaxRecordsPerFrame)
        {
            var chunk = new List<FuncObject>(Math.Min(MaxRecordsPerFrame, objects.Count - i));
            for (int j = i; j < objects.Count && j < i + MaxRecordsPerFrame; j++) chunk.Add(objects[j]);
            frames.Add(BuildFuncObjectIn2512(chunk));
        }
        return frames;
    }
}
