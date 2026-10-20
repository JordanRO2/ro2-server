namespace RO2.Domain.Entities;

/// <summary>
/// A character's saved key bindings, exactly as its client last sent them in C2S 1423
/// Req_CLZSR_UPDATE_KEYCONFIG. <see cref="Id"/> is the character id (one record per character).
/// </summary>
public sealed class KeyConfigDocument
{
    /// <summary>Character id (the record key, one document per character).</summary>
    public uint Id { get; set; }

    /// <summary>The u16 at 1423 blob+4, written back as the 2558 flag byte. 0 with no records means
    /// "clear the flag, keep the list".</summary>
    public ushort Mode { get; set; }

    /// <summary>The binding records, <c>count × 4</c> bytes verbatim:
    /// <c>{u8 vkCode, u8 vkCode2, u16 actionId}</c> (Windows virtual-key codes, e.g. 0x68 NUMPAD8).</summary>
    public byte[] Entries { get; set; } = Array.Empty<byte>();

    public DateTime UpdatedUtc { get; set; }
}
