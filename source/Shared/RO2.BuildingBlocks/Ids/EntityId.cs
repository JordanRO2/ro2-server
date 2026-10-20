namespace RO2.BuildingBlocks.Domain;

/// <summary>
/// Strongly-typed identifier for a runtime world entity (object id / OID).
/// </summary>
/// <remarks>
/// Wraps the underlying <see cref="uint"/> runtime OID. For NPCs this is the
/// deterministic value <c>mapId&lt;&lt;16 | spawnIndex</c>, owned by the World service.
/// Using a dedicated type prevents an <see cref="EntityId"/> from being accidentally
/// passed where another id (e.g. a <see cref="CharacterId"/>) is expected.
/// </remarks>
/// <param name="Value">The wrapped raw runtime entity id.</param>
public readonly record struct EntityId(uint Value)
{
 /// <summary>Creates an <see cref="EntityId"/> from a raw <see cref="uint"/> value.</summary>
 /// <param name="value">The raw runtime entity id.</param>
 /// <returns>An <see cref="EntityId"/> wrapping <paramref name="value"/>.</returns>
    public static EntityId From(uint value) => new(value);

 /// <summary>Implicitly unwraps an <see cref="EntityId"/> to its raw <see cref="uint"/> value.</summary>
 /// <param name="id">The identifier to unwrap.</param>
    public static implicit operator uint(EntityId id) => id.Value;

 /// <summary>Explicitly wraps a raw <see cref="uint"/> value as an <see cref="EntityId"/>.</summary>
 /// <param name="value">The raw runtime entity id.</param>
    public static explicit operator EntityId(uint value) => new(value);

 /// <summary>Returns the raw value rendered as a string.</summary>
 /// <returns>The decimal string representation of <see cref="Value"/>.</returns>
    public override string ToString() => Value.ToString();
}
