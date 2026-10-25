using RO2.BuildingBlocks;

namespace RO2.Economy.Domain;

/// <summary>
/// One stack sold to an NPC shop and kept on the player's buy-back list: <see cref="Item"/> is a copy taken at the
/// sale with its count set to the quantity sold (the whole item is kept, because a fully sold stack leaves the
/// inventory), and <see cref="Paid"/> is the zeny the sale paid, which is what buying it back charges.
/// </summary>
/// <param name="Item">The sold stack, with <see cref="ItemStackSnapshot.Count"/> set to the quantity sold.</param>
/// <param name="Paid">The zeny the sale paid.</param>
public sealed record BuyBackEntry(ItemStackSnapshot Item, long Paid);
