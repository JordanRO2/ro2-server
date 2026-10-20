namespace RO2.Domain.Entities;

/// <summary>One purchase in an account's history, a 2874 record.</summary>
public sealed class CashMallPurchase
{
 /// <summary>The 2870 entry's order id (CashMallStore.OrderCounter).</summary>
    public uint OrderId { get; set; }
    public uint ItemId { get; set; }
    public uint ProductId { get; set; }
    public ushort Quantity { get; set; }

 /// <summary>The unit price charged (the 2874 record's f32 is the unit price, not the line total).</summary>
    public long UnitPrice { get; set; }

    public DateTime BoughtUtc { get; set; }
    public bool Gift { get; set; }

 /// <summary>Gift only: the buyer's character name and the recipient's, as the 2874 record lists them.</summary>
    public string SenderName { get; set; } = "";
    public string RecipientName { get; set; } = "";
    public uint RecipientAccountId { get; set; }
}
