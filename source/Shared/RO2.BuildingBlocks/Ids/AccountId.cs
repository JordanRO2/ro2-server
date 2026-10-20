namespace RO2.BuildingBlocks.Domain;

/// <summary>
/// Strongly-typed identifier for a user account.
/// </summary>
/// <remarks>
/// Wraps the underlying <see cref="uint"/> account id (the wire encodes it as 32-bit).
/// Using a dedicated type prevents an <see cref="AccountId"/> from being accidentally
/// passed where another id (e.g. a <see cref="CharacterId"/>) is expected.
/// </remarks>
/// <param name="Value">The wrapped raw account id.</param>
public readonly record struct AccountId(uint Value)
{
 /// <summary>Creates an <see cref="AccountId"/> from a raw <see cref="uint"/> value.</summary>
 /// <param name="value">The raw account id.</param>
 /// <returns>An <see cref="AccountId"/> wrapping <paramref name="value"/>.</returns>
    public static AccountId From(uint value) => new(value);

 /// <summary>Implicitly unwraps an <see cref="AccountId"/> to its raw <see cref="uint"/> value.</summary>
 /// <param name="id">The identifier to unwrap.</param>
    public static implicit operator uint(AccountId id) => id.Value;

 /// <summary>Explicitly wraps a raw <see cref="uint"/> value as an <see cref="AccountId"/>.</summary>
 /// <param name="value">The raw account id.</param>
    public static explicit operator AccountId(uint value) => new(value);

 /// <summary>Returns the raw value rendered as a string.</summary>
 /// <returns>The decimal string representation of <see cref="Value"/>.</returns>
    public override string ToString() => Value.ToString();
}
