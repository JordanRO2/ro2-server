using RO2.BuildingBlocks;

namespace RO2.Economy.Domain;

/// <summary>
/// What a trade side put in the window for one offered uid, frozen at the moment the 1353 was accepted: the
/// item instance's template, the offered quantity and every per-instance property the partner sees in the 2384
/// record (refine, element, bind history, the three dyes, the FX-enchant and timed-enchant state, rune holes
/// and their punchers).
/// </summary>
/// <remarks>
/// A uid names a row, not an item: the next pickup reuses a freed uid, so the row under an offered uid can be
/// sold or consumed and replaced by a different template (or a re-refined / re-socketed / re-dyed instance) while
/// the partner still looks at the old record. The confirm-time swap rebuilds this from the live row with
/// <see cref="Of"/> and moves nothing unless it equals what was offered (E-H4). <see cref="Qty"/> is the OFFERED
/// quantity, not the stack size, so a stack that merely grew since the offer does not abort a trade; one that
/// shrank below the offer is caught by the quantity check beside the comparison.
/// </remarks>
/// <param name="Uid">The item instance uid.</param>
/// <param name="ItemId">The template id.</param>
/// <param name="Qty">The offered quantity.</param>
/// <param name="Refine">Refine level.</param>
/// <param name="Element">Bestowed element.</param>
/// <param name="Bound">Bind history.</param>
/// <param name="DyeColor">Primary dye.</param>
/// <param name="DyeColor2">Second dye.</param>
/// <param name="DyeColor3">Third dye.</param>
/// <param name="FxEnchantIndex">FX-enchant index.</param>
/// <param name="EnchantReinforceId">Enchant/reinforce id.</param>
/// <param name="EnchantSeconds">Remaining timed-enchant seconds.</param>
/// <param name="CardsJson">Rune holes, as JSON.</param>
/// <param name="SocketPunchersJson">Rune-hole punchers, as JSON.</param>
public readonly record struct TradeOfferSnapshot(
    ulong Uid, uint ItemId, int Qty, byte Refine, byte Element, bool Bound,
    uint DyeColor, uint DyeColor2, uint DyeColor3,
    uint FxEnchantIndex, ushort EnchantReinforceId, ushort EnchantSeconds,
    string? CardsJson, string? SocketPunchersJson)
{
    /// <summary>The snapshot of <paramref name="item"/> as offered with <paramref name="qty"/> units.</summary>
    /// <param name="item">The item as stored.</param>
    /// <param name="qty">The offered quantity.</param>
    public static TradeOfferSnapshot Of(ItemStackSnapshot item, int qty) => new(
        item.Id, item.ItemId, qty, item.Refine, item.Element, item.Bound,
        item.DyeColor, item.DyeColor2, item.DyeColor3,
        item.FxEnchantIndex, item.EnchantReinforceId, item.EnchantSeconds,
        item.CardsJson, item.SocketPunchersJson);
}
