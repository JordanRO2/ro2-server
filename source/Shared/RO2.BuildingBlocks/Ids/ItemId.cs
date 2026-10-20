namespace RO2.BuildingBlocks.Domain;

/// <summary>
/// Strongly-typed identifier for an item template (catalog/definition id).
/// </summary>
/// <remarks>
/// Wraps the underlying <see cref="uint"/> item template id (e.g.
/// <c>InventoryItem.ItemId</c>). This identifies the kind of item, not a specific
/// stack instance (that is the item instance id). Using a dedicated type prevents an
/// <see cref="ItemId"/> from being accidentally passed where another id is expected.
/// </remarks>
/// <param name="Value">The wrapped raw item template id.</param>
public readonly record struct ItemId(uint Value)
{
 /// <summary>Creates an <see cref="ItemId"/> from a raw <see cref="uint"/> value.</summary>
 /// <param name="value">The raw item template id.</param>
 /// <returns>An <see cref="ItemId"/> wrapping <paramref name="value"/>.</returns>
    public static ItemId From(uint value) => new(value);

 /// <summary>Implicitly unwraps an <see cref="ItemId"/> to its raw <see cref="uint"/> value.</summary>
 /// <param name="id">The identifier to unwrap.</param>
    public static implicit operator uint(ItemId id) => id.Value;

 /// <summary>Explicitly wraps a raw <see cref="uint"/> value as an <see cref="ItemId"/>.</summary>
 /// <param name="value">The raw item template id.</param>
    public static explicit operator ItemId(uint value) => new(value);

 /// <summary>Returns the raw value rendered as a string.</summary>
 /// <returns>The decimal string representation of <see cref="Value"/>.</returns>
    public override string ToString() => Value.ToString();
}
