namespace RO2.Network.Packets;

/// <summary>
/// Controls whether <see cref="CompactEnvelope.TryGetCompactBlob"/> checks the declared blob length.
/// </summary>
/// <remarks>
/// The check is chosen per call site. Some handlers accept frames whose declared length does not match the byte count, so the two behaviours are kept separate rather than merged into one default.
/// </remarks>
public enum CompactLengthCheck
{
 /// <summary>The declared length must equal the number of bytes after the prefix.</summary>
    Strict,

 /// <summary>The declared length is ignored; the blob is everything after the prefix.</summary>
    Lenient,
}

/// <summary>
/// The compact-length byte array envelope: a width marker, the length in little-endian bytes, then the blob.
/// </summary>
/// <remarks>
/// Layout: <c>[u8 width in {1,2,4,8}][width bytes LE = N][N-byte blob]</c>. The writer is <see cref="PacketWriter.WriteCompactLength"/>. The blob usually starts with a 2-byte inner size, a 2-byte message tag and a 4-byte handle.
/// This is the single shared parser for the envelope. Each caller selects <see cref="CompactLengthCheck.Strict"/> or <see cref="CompactLengthCheck.Lenient"/> to match its behaviour.
/// </remarks>
public static class CompactEnvelope
{
 /// <summary>
 /// Returns true for the four legal width markers (1, 2, 4 or 8). Any other marker means the body is not an enveloped frame.
 /// </summary>
    public static bool IsWidthMarker(byte marker) => marker is 1 or 2 or 4 or 8;

    extension(ReadOnlySpan<byte> body)
    {
 /// <summary>
 /// Reads a compact length (<c>[u8 width in {1,2,4,8}][width bytes LE]</c>) at the start of the span.
 /// </summary>
 /// <param name="value">The decoded value, or 0 on failure.</param>
 /// <param name="size">Bytes the length occupies (1 + width), or 0 on failure.</param>
 /// <returns>False when the span is empty, the width marker is illegal, or the span is shorter than the declared width.</returns>
 /// <remarks>
 /// The value is a generic length: it can be a byte count, an element count or a character count, depending on the field.
 /// </remarks>
        public bool TryReadCompactLength(out ulong value, out int size)
        {
            value = 0;
            size = 0;
            if (body.IsEmpty) return false;
            byte width = body[0];
            if (!IsWidthMarker(width) || body.Length < 1 + width) return false;
            ulong v = 0;
            for (int i = 0; i < width; i++) v |= (ulong)body[1 + i] << (8 * i);
            value = v;
            size = 1 + width;
            return true;
        }

 /// <summary>
 /// Strips the compact-length prefix and returns the blob behind it.
 /// </summary>
 /// <param name="check">Whether the declared length must match the remaining bytes (<see cref="CompactLengthCheck.Strict"/>) or is ignored (<see cref="CompactLengthCheck.Lenient"/>). Any other value is treated as strict.</param>
 /// <param name="blob">Everything after the prefix, or empty on failure.</param>
 /// <returns>False when the prefix is malformed or, in strict mode, when the declared length differs from the remaining byte count.</returns>
        public bool TryGetCompactBlob(CompactLengthCheck check, out ReadOnlySpan<byte> blob)
        {
            blob = default;
            if (!body.TryReadCompactLength(out ulong declared, out int prefix)) return false;
            var rest = body[prefix..];
            
            
            if (check != CompactLengthCheck.Lenient && declared != (ulong)rest.Length) return false;
            blob = rest;
            return true;
        }
    }
}
