namespace RO2.Domain.Entities;

/// <summary>One gift in an account's gift box ("Item Box Storage"), as its 43-byte 2799 record lists it.</summary>
public sealed class AccountGift
{
 /// <summary>The 16 bytes the 2799 record carries at @0 and the 1551 claim names, as 32 hex digits.</summary>
    public string Guid { get; set; } = "";

 /// <summary>ItemInfo id (record @16).</summary>
    public uint ItemId { get; set; }

 /// <summary>How many (record @20).</summary>
    public ushort Count { get; set; }

 /// <summary>When it was filed, UTC. The wire carries it as a server-local OLE date (record @22).</summary>
    public DateTime ReceivedUtc { get; set; }

 /// <summary>What filed it (record @39): 1 for a play-time reward, 4 for a daily attendance reward.</summary>
    public byte Source { get; set; }
}
