using RO2.BuildingBlocks;

namespace RO2.Economy.Application;

/// <summary>
/// The inventory operations the economy flows need, atomic under the per-character inventory gate. The host
/// implements this over its inventory repository; the economy application depends only on this contract, never
/// on the game's inventory row type. Every call serialises against other mutations of the same character, so a
/// stack a trade/sale/mail attach took meanwhile is taken by nobody.
/// </summary>
public interface IInventoryEscrow
{
    /// <summary>
    /// Takes <paramref name="count"/> of the stack <paramref name="itemUid"/> out of <paramref name="characterId"/>'s
    /// bag into escrow, atomically. Returns the taken snapshot and the count left in the bag, or a null snapshot when
    /// the stack is gone, owned by someone else, not in the bag, or smaller than <paramref name="count"/>.
    /// </summary>
    Task<(ItemStackSnapshot? Taken, int CountLeft)> TakeAsync(uint characterId, ulong itemUid, int count);

    /// <summary>
    /// Restores a previously escrowed item to the bag (listing cancelled, expired unsold, or a failure after the
    /// take). <paramref name="countLeft"/> is what <see cref="TakeAsync"/> reported and <paramref name="count"/> is
    /// the escrowed amount, so the implementation can re-merge a partial take or re-add a whole one.
    /// </summary>
    Task RestoreAsync(uint characterId, ItemStackSnapshot taken, int countLeft, int count);

    /// <summary>Places an item into <paramref name="characterId"/>'s bag (a won, bought or returned item), applying
    /// the stacking rules. Used by the payout/purchase paths.</summary>
    Task GiveAsync(uint characterId, ItemStackSnapshot item);
}
