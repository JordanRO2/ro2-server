namespace RO2.Economy.Domain;

/// <summary>One item a stall lists, as the economy sees it: the list index the buyer names, the listed item instance,
/// and the quantity and price offered.</summary>
/// <param name="Index">The list index given to buyers (a u8 on the wire).</param>
/// <param name="Uid">The listed item instance in the seller's bag.</param>
/// <param name="Count">The listed quantity.</param>
/// <param name="Price">The price of the whole listing (not per unit).</param>
public sealed record StallListing(byte Index, ulong Uid, ushort Count, ulong Price);
