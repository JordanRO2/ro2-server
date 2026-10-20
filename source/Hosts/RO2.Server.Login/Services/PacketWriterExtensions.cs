using System.Text;
using RO2.Network.Packets;

namespace RO2.Server.Login.Services;

/// <summary>
/// Extension methods for PacketWriter.
/// </summary>
public static class PacketWriterExtensions
{
 /// <summary>
 /// Writes a wide string (UTF-16LE) with a fixed character count.
 /// </summary>
    public static void WriteWideStringFixed(this PacketWriter writer, string value, int charCount)
    {
        var bytes = Encoding.Unicode.GetBytes(value);
        var maxBytes = charCount * 2;

        
        var bytesToWrite = Math.Min(bytes.Length, maxBytes - 2); 
        writer.WriteBytes(bytes.AsSpan(0, bytesToWrite));

        
        var remaining = maxBytes - bytesToWrite;
        for (int i = 0; i < remaining; i++)
            writer.WriteByte(0);
    }
}
