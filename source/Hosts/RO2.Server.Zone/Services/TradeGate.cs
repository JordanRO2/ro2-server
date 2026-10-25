using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Shared rule for whether an item instance may change hands. Pure: no database or sockets.
/// Trade enforces it today; mail, auction, private shop and guild vault are expected to use the same method.
/// </summary>
public static class TradeGate
{
 /// <summary>
 /// True when the item must be refused as untradeable: its template is flagged untradeable (Is_Trade 0),
 /// or the instance is bound (Bind_Type 2, or Bind_Type 1 once worn). The rule itself lives in
 /// <see cref="ItemTradeRules.RefusedAsNotTradeable(byte, byte, byte, bool)"/>; this reads the primitives
 /// off the template and instance.
 /// </summary>
    public static bool RefusedAsNotTradeable(ItemData template, InventoryItem item)
        => ItemTradeRules.RefusedAsNotTradeable(template.Is_Trade, template.Bind_Type, item.StorageType, item.Bound);
}
