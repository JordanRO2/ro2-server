namespace RO2.Economy.Domain;

/// <summary>Why an NPC-shop buy is refused, or <see cref="None"/> when it passes the catalog check.</summary>
public enum ShopPurchaseRefusal
{
    /// <summary>The buy passes: a live shop NPC of the player's map stocks the item (and the merchant row fits).</summary>
    None,

    /// <summary>The named handle is no live NPC of the player's map (unresolved, dead, or cross-map).</summary>
    NpcUnavailable,

    /// <summary>That NPC runs no shop at all.</summary>
    NoShop,

    /// <summary>That NPC's shop does not stock the item.</summary>
    NotStocked,

    /// <summary>The merchant table is loaded but the buy names no merchant row.</summary>
    NoMerchantRow,

    /// <summary>The named merchant row belongs to another shop.</summary>
    WrongMerchantRow,
}

/// <summary>
/// Pure NPC-shop catalog rule: given the facts the host has resolved (whether the handle is a live shop NPC on
/// the player's map, the NPC's shop stock, and the named merchant row), decide whether a buy is allowed. It knows
/// nothing of the game data, the NPC registry, or the wire — the host resolves the facts and maps the verdict back
/// to its wire code and log reason.
/// </summary>
public static class ShopPurchaseRules
{
    /// <summary>
    /// The first failing guard of a buy, in order, or <see cref="ShopPurchaseRefusal.None"/> when it is allowed.
    /// </summary>
    /// <param name="npcLiveOnMap">Whether the named handle resolved to a live NPC on the player's map.</param>
    /// <param name="shopStockCount">Number of items the NPC's shop stocks (0 = no shop).</param>
    /// <param name="shopStocksItem">Whether that shop stocks the requested item.</param>
    /// <param name="merchantTableLoaded">Whether the Merchant table is loaded (so a row is required and checked).</param>
    /// <param name="merchantRowProvided">Whether the buy named a merchant row.</param>
    /// <param name="merchantRowBelongsToShop">Whether the named row is one of the NPC's own shop rows.</param>
    public static ShopPurchaseRefusal CheckPurchase(
        bool npcLiveOnMap, int shopStockCount, bool shopStocksItem,
        bool merchantTableLoaded, bool merchantRowProvided, bool merchantRowBelongsToShop)
    {
        if (!npcLiveOnMap) return ShopPurchaseRefusal.NpcUnavailable;
        if (shopStockCount == 0) return ShopPurchaseRefusal.NoShop;
        if (!shopStocksItem) return ShopPurchaseRefusal.NotStocked;
        if (merchantTableLoaded)
        {
            if (!merchantRowProvided) return ShopPurchaseRefusal.NoMerchantRow;
            if (!merchantRowBelongsToShop) return ShopPurchaseRefusal.WrongMerchantRow;
        }
        return ShopPurchaseRefusal.None;
    }
}
