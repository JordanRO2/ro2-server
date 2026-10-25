using System.Buffers.Binary;
using RO2.Domain.Entities;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Wire codec for the key-config packets: parses the C2S 1423 update body and builds the S2C 2558 account-config
/// payload from a stored <see cref="KeyConfigDocument"/>.
/// </summary>
public static class KeyConfigCodec
{
 /// <summary>
 /// Parses a 1423 update body: a u32 length, a u32 actor handle, a u16 mode, a u16 entry count, then four bytes per entry.
 /// Bytes after the declared length are ignored.
 /// </summary>
    public static bool TryParseUpdate(ReadOnlySpan<byte> body, out ushort mode, out byte[] entries)
    {
        mode = 0;
        entries = Array.Empty<byte>();
        if (body.Length < 12) return false;
        var len = BinaryPrimitives.ReadUInt32LittleEndian(body);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(10));
        if (len != 8u + 4u * count || body.Length < 4 + len) return false;
        mode = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(8));
        entries = body.Slice(12, 4 * count).ToArray();
        return true;
    }

 /// <summary>
 /// Builds the 2558 account-config payload from the character's saved bindings, or the empty 11-byte form when
 /// <paramref name="config"/> is null. The flag byte is the low byte of the saved mode.
 /// </summary>
    public static byte[] BuildAccountConfig2558(uint pcHandle, KeyConfigDocument? config)
    {
        var entries = config?.Entries ?? Array.Empty<byte>();
        var count = entries.Length / 4;
        var body = new byte[11 + 4 * count];
        BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)(7 + 4 * count));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), pcHandle);
        body[8] = (byte)(config?.Mode ?? 0);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(9), (ushort)count);
        entries.AsSpan(0, 4 * count).CopyTo(body.AsSpan(11));
        return body;
    }
}
