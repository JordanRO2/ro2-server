namespace RO2.Domain.Entities;

/// <summary>One entry of an account's web-mall storage box, the row 2639 (box 1) / 2640 (box 2) list.</summary>
public sealed class CashMallBoxEntry
{
 /// <summary>The u32 the list names and 1472 ReqWebItemAtMyInven claims: a server-wide counter
 /// (CashMallStore.EntryCounter; retail's run near 1.87M, e.g. ).</summary>
    public uint EntryId { get; set; }

 /// <summary>CashMallRules.BoxPurchased or CashMallRules.BoxGift.</summary>
    public byte Box { get; set; }

    public uint ItemId { get; set; }

 /// <summary>Units still waiting (the list's u8 count).</summary>
    public int Count { get; set; }

 /// <summary>When it was filed, UTC. The wire carries it as a server-local OLE date.</summary>
    public DateTime FiledUtc { get; set; }

 /// <summary>Box 2 only: the character that sent the gift (2640 +25, 2641 +26).</summary>
    public string SenderName { get; set; } = "";
}
