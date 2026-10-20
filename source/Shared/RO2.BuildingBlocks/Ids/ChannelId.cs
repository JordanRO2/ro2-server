namespace RO2.BuildingBlocks.Domain;

/// <summary>
/// Strongly-typed identifier for a community channel.
/// </summary>
/// <remarks>
/// Wraps the underlying <see cref="ushort"/> community channel index (e.g. 12/13/14).
/// This is the logical channel index, not the packed 32-bit server-group code used by
/// the login server-group/select-server messages. Using a dedicated type prevents a
/// <see cref="ChannelId"/> from being accidentally passed where another id is expected.
/// </remarks>
/// <param name="Value">The wrapped raw community channel index.</param>
public readonly record struct ChannelId(ushort Value)
{
 /// <summary>Creates a <see cref="ChannelId"/> from a raw <see cref="ushort"/> value.</summary>
 /// <param name="value">The raw community channel index.</param>
 /// <returns>A <see cref="ChannelId"/> wrapping <paramref name="value"/>.</returns>
    public static ChannelId From(ushort value) => new(value);

 /// <summary>Implicitly unwraps a <see cref="ChannelId"/> to its raw <see cref="ushort"/> value.</summary>
 /// <param name="id">The identifier to unwrap.</param>
    public static implicit operator ushort(ChannelId id) => id.Value;

 /// <summary>Explicitly wraps a raw <see cref="ushort"/> value as a <see cref="ChannelId"/>.</summary>
 /// <param name="value">The raw community channel index.</param>
    public static explicit operator ChannelId(ushort value) => new(value);

 /// <summary>Returns the raw value rendered as a string.</summary>
 /// <returns>The decimal string representation of <see cref="Value"/>.</returns>
    public override string ToString() => Value.ToString();
}
