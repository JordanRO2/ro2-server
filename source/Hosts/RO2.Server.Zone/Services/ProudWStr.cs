using System.Text;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Reader for the ProudNet WStr primitive: <c>[u8 widthTag][compact length nChars][nChars * widthTag bytes]</c>.
/// A width tag of 2 is UTF-16LE and 1 is a single-byte string. This is not the same as WStr16, which has a
/// <c>[u16 n][2n]</c> layout.
/// </summary>
public static class ProudWStr
{
 /// <summary>Reads one WStr at <paramref name="pos"/> and advances it. Returns false on a short or malformed
 /// string, or one longer than <paramref name="maxChars"/>; the position is unspecified on failure.</summary>
    public static bool TryRead(ReadOnlySpan<byte> b, ref int pos, out string value, int maxChars = 64)
    {
        value = string.Empty;
        if (pos < 0 || pos + 2 > b.Length) return false;
        byte width = b[pos++];
        if (width != 1 && width != 2) return false;
        
        
        if (!b[pos..].TryReadCompactLength(out ulong n, out int compactSize)) return false;
        pos += compactSize;
        if (n > (ulong)maxChars) return false;          
        int bytes = (int)n * width;
        if (pos + bytes > b.Length) return false;
        value = width == 2
            ? Encoding.Unicode.GetString(b.Slice(pos, bytes))
            : Encoding.Latin1.GetString(b.Slice(pos, bytes));
        pos += bytes;
        return true;
    }
}
