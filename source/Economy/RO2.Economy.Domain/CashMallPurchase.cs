namespace RO2.Economy.Domain;

/// <summary>One purchase in an account's history, a 2874 record.</summary>
public sealed class CashMallPurchase
{
 /// <summary>The 2870 entry's order id (CashMallStore.OrderCounter).</summary>
    public uint OrderId { get; set; }
 /// <summary>The item bought.</summary>
    public uint ItemId { get; set; }
 /// <summary>The mall product id the item was sold under.</summary>
    public uint ProductId { get; set; }
 /// <summary>Units bought in this purchase.</summary>
    public ushort Quantity { get; set; }

 /// <summary>The unit price charged (the 2874 record's f32 is the unit price, not the line total).</summary>
    public long UnitPrice { get; set; }

 /// <summary>When the purchase was made, UTC.</summary>
    public DateTime BoughtUtc { get; set; }
 /// <summary>True when this purchase was a gift to another account.</summary>
    public bool Gift { get; set; }

 /// <summary>Gift only: the buyer's character name and the recipient's, as the 2874 record lists them.</summary>
    public string SenderName { get; set; } = "";
 /// <summary>Gift only: the recipient's character name.</summary>
    public string RecipientName { get; set; } = "";
 /// <summary>Gift only: the recipient's account id.</summary>
    public uint RecipientAccountId { get; set; }
}
