namespace RO2.BuildingBlocks.Domain;

/// <summary>
/// Strongly-typed identifier for a player character.
/// </summary>
/// <remarks>
/// Wraps the underlying <see cref="uint"/> character id (the Zone-issued charUid,
/// echoed by Chat). Using a dedicated type prevents a <see cref="CharacterId"/>
/// from being accidentally passed where another id (e.g. an <see cref="AccountId"/>)
/// is expected.
/// </remarks>
/// <param name="Value">The wrapped raw character id.</param>
public readonly record struct CharacterId(uint Value)
{
 /// <summary>Creates a <see cref="CharacterId"/> from a raw <see cref="uint"/> value.</summary>
 /// <param name="value">The raw character id.</param>
 /// <returns>A <see cref="CharacterId"/> wrapping <paramref name="value"/>.</returns>
    public static CharacterId From(uint value) => new(value);

 /// <summary>Implicitly unwraps a <see cref="CharacterId"/> to its raw <see cref="uint"/> value.</summary>
 /// <param name="id">The identifier to unwrap.</param>
    public static implicit operator uint(CharacterId id) => id.Value;

 /// <summary>Explicitly wraps a raw <see cref="uint"/> value as a <see cref="CharacterId"/>.</summary>
 /// <param name="value">The raw character id.</param>
    public static explicit operator CharacterId(uint value) => new(value);

 /// <summary>Returns the raw value rendered as a string.</summary>
 /// <returns>The decimal string representation of <see cref="Value"/>.</returns>
    public override string ToString() => Value.ToString();
}
