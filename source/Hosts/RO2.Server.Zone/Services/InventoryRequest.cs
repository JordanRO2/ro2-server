namespace RO2.Server.Zone.Services;

/// <summary>Why <see cref="InventoryRequest.Validate"/> refused an inventory request; <see cref="None"/> = accepted.</summary>
public enum InvReject
{
    None,
    BadMsgTag,
    HandleMismatch,
    BadContainer,
    SlotOutOfRange,
    BadQuantity,
}

/// <summary>
/// One pure input check for the inventory C2S blobs (1324 ReqPCItemSwap, 1346 ReqBankSwapItem, 1323 use, ...): the
/// <c>blob.Length</c> checks of those handlers do not cover the message tag, the actor handle, the container, the
/// slot or the quantity bytes. Call it after the length check and before any repository read.
/// </summary>
/// <remarks>
/// Wire facts: container 0/1 slot 0..49 = worn window, 50..69 = main bag, 70..72 = character-bag window;
/// containers 2..4 = extra bags, slot below that bag's Bag_Size; container 5 = bank (1346 only). The extra-bag
/// bound used here is the flat-stride ceiling <see cref="BagCapacity.ExtraBagStride"/> (35, the largest Bag_Size
/// the geometry holds), so it is deliberately CONSERVATIVE: the exact "slot &lt; the equipped bag's Bag_Size" check
/// (and "is a bag equipped in that container at all") needs the character's rows and stays with the caller. The
/// bank bound is <see cref="EquipRules.MaxBankSlot"/>; the caller's per-character BankExpansion check is the
/// tighter one. UNVERIFIED: the tag, handle and quantity checks are defensive rules.
/// </remarks>
public static class InventoryRequest
{
    /// <summary>Highest slot byte of the character-bag window (70..72, three equippable bags).</summary>
    public const int MaxCharBagWindowSlot = 72;

    /// <summary>Highest container of the main window (containers 0 and 1 share the worn / main bag / character-bag window).</summary>
    private const byte MaxMainWindowContainer = 1;

    /// <summary>Highest extra-bag container (containers 2..4); container 5 is the bank.</summary>
    public const byte MaxExtraBagContainer = 4;

    /// <summary>
    /// Checks, in this order: <paramref name="msgTag"/> == <paramref name="expectedTag"/>; <paramref name="handle"/>
    /// == <paramref name="playerEntityId"/>; <paramref name="container"/> in 0..<paramref name="maxContainer"/>
    /// (4 for 1324, 5 for the 1346 bank); <paramref name="slot"/> inside the container's wire bound; and, where the
    /// request carries a quantity, <paramref name="qty"/> in 1..<paramref name="stackCount"/>.
    /// </summary>
    /// <param name="slot">The raw wire slot byte as an int. A caller that accepts the 0xFF "any free cell" destination
    /// of 1346 must handle it before calling (pass 0), it is not a legal cell.</param>
    /// <param name="qty">Requested amount; pass 1 with <paramref name="stackCount"/> 1 when the request has no
    /// quantity field.</param>
    /// <param name="stackCount">The source stack's Count.</param>
    public static InvReject Validate(ushort msgTag, ushort expectedTag, uint handle, uint playerEntityId,
        byte container, int slot, int qty, int stackCount, byte maxContainer = MaxExtraBagContainer)
    {
        if (msgTag != expectedTag) return InvReject.BadMsgTag;
        if (handle != playerEntityId) return InvReject.HandleMismatch;
        if (container > maxContainer) return InvReject.BadContainer;

        int maxSlot = container switch
        {
            <= MaxMainWindowContainer => MaxCharBagWindowSlot,
            <= MaxExtraBagContainer => BagCapacity.ExtraBagStride - 1,
            _ => EquipRules.MaxBankSlot,
        };
        if (slot < 0 || slot > maxSlot) return InvReject.SlotOutOfRange;

        if (qty < 1 || qty > stackCount) return InvReject.BadQuantity;
        return InvReject.None;
    }
}
