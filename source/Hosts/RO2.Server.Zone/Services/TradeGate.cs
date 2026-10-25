using RO2.Domain.Entities;
using RO2.Domain.GameData;
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
 /// or the instance is bound. An instance is bound when <see cref="WorldEntryService.RecordBindFlags(byte, byte, bool)"/>
 /// sets <see cref="WorldEntryService.RecordBoundFlag"/>: Bind_Type 2, or Bind_Type 1 once worn.
 /// </summary>
    public static bool RefusedAsNotTradeable(ItemData template, InventoryItem item)
        => template.Is_Trade == 0
            || WorldEntryService.RecordBindFlags(template.Bind_Type, item.StorageType, item.Bound) == WorldEntryService.RecordBoundFlag;
}
