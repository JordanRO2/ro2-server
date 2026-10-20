namespace RO2.Domain.Entities;

/// <summary>
/// One entry in a character's card book: a single registered card, with its Grade_Type, the enforce grade
/// it carried when it was put in and (for audit) the uid of the inventory item that was consumed to
/// register it. The physical InventoryItem row is DELETED on registration and RE-CREATED (at the same
/// enforce grade) on take-off, so the card book is the sole owner of the card while it is registered —
/// that is what keeps the move from ever duplicating the item.
/// </summary>
public sealed class CardBookEntry
{
    /// <summary>Card_ID (== the card item's template id, e.g. 22310001). Also the value the book list sends.</summary>
    public uint CardId { get; set; }

    /// <summary>Grade_Type from CardInfo, echoed in the 8-byte book-list record.</summary>
    public byte Grade { get; set; }

    /// <summary>
    /// Enforce grade of this copy: the put stack's InventoryItem.Refine (item record offset +0x1D). The book is
    /// keyed on it: the grade byte of a put (RMI 2589) equals that offset of the put stack, the copy count in
    /// RMI 2589 follows (card, this grade), and a take-off (RMI 1552) hands the card back at the requested grade.
    /// Kept apart from <see cref="Grade"/>, which holds Grade_Type 2..5. An entry stored before this field
    /// existed has no element for it and loads as 0.
    /// </summary>
    public byte EnforceGrade { get; set; }

    /// <summary>
    /// Byte 7 of this copy's book-list record, 0 or 1: the put request's body offset +12, echoed by the put
    /// answer (RMI 2589). The book lists copies with byte 7 = 1 apart from copies with byte 7 = 0, and the client
    /// keys its copies on (card, enforce grade, byte 7 != 0). An entry stored before this field existed has no
    /// element for it and loads as 0.
    /// </summary>
    public byte Kind { get; set; }

    /// <summary>Uid of the inventory item consumed when this card was registered (reference/audit only).</summary>
    public ulong OriginItemUid { get; set; }

    public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;
}
