namespace RO2.Domain.Entities;

/// <summary>One guild's registered emblem: the image the master uploaded with 1455 and its emblem index.</summary>
public sealed class GuildEmblem
{
 /// <summary>The guild GUID as 32 lowercase hex chars, the document id. Emblems are addressed by GUID on
 /// the wire (1455, 2612, 2614 all carry it), and keying by it rather than by the numeric guild id means a
 /// reused guild id can never inherit a disbanded guild's emblem.</summary>
    public string Id { get; set; } = string.Empty;

 /// <summary>The emblem index 2612/2614/2559/2591 carry. It is a revision counter, not a picture id, and
 /// advances by one on every upload; 0 means "no emblem".</summary>
    public ushort Index { get; set; }

 /// <summary>The image bytes exactly as uploaded (a DDS file; 640 bytes in every captured image).</summary>
    public byte[] Image { get; set; } = Array.Empty<byte>();

    public DateTime UpdatedUtc { get; set; }
}
