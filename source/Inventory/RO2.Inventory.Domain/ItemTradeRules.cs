namespace RO2.Inventory.Domain;

/// <summary>
/// Pure item-instance mechanics over primitives: whether an instance is bound to its owner, and whether it
/// must be refused when it would change hands. Trade, auction, private shop, mail and the item record all
/// decide these the same way; callers pass the template flags and the instance's storage and bind state in,
/// so the rule knows nothing of the game-data model, the wire, or the database.
/// </summary>
public static class ItemTradeRules
{
    /// <summary>Bind type of a template that binds its item when the item is equipped.</summary>
    public const byte BindOnEquip = 1;

    /// <summary>Bind type of a template that binds its item as soon as it is acquired.</summary>
    public const byte BindOnAcquire = 2;

    /// <summary>Storage type of a worn item.</summary>
    public const byte StorageWorn = 1;

    /// <summary>Storage type of a bag worn in the character-bag window.</summary>
    public const byte StorageEquippedBag = 3;

    /// <summary>
    /// True when an item instance is bound to its owner: its template is <see cref="BindOnAcquire"/>, or it
    /// is <see cref="BindOnEquip"/> and is currently worn, in an equipped bag, or has been worn at some point.
    /// </summary>
    /// <param name="bindType">Bind type of the item template.</param>
    /// <param name="storageType">Storage location of the item (1 worn, 3 equipped bag).</param>
    /// <param name="everWorn">Whether the item was worn at some point.</param>
    public static bool IsBound(byte bindType, byte storageType, bool everWorn)
        => bindType == BindOnAcquire
            || (bindType == BindOnEquip && (storageType is StorageWorn or StorageEquippedBag || everWorn));

    /// <summary>
    /// True when the item must be refused as untradeable: its template is flagged untradeable
    /// (<paramref name="isTrade"/> is 0), or the instance <see cref="IsBound(byte, byte, bool)"/>.
    /// </summary>
    /// <param name="isTrade">The template's <c>Is_Trade</c> flag (0 = untradeable).</param>
    /// <param name="bindType">Bind type of the item template.</param>
    /// <param name="storageType">Storage location of the item.</param>
    /// <param name="bound">Whether the instance has been worn (its persisted bound flag).</param>
    public static bool RefusedAsNotTradeable(byte isTrade, byte bindType, byte storageType, bool bound)
        => isTrade == 0 || IsBound(bindType, storageType, bound);
}
