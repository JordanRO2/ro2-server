namespace RO2.BuildingBlocks.Domain.ValueObjects;

/// <summary>
/// An accumulated experience amount (base EXP or job EXP), stored as an unsigned 64-bit value to match the
/// wire/persistence representation (<c>Character.Exp</c> / <c>Character.JobExp</c> are <see cref="ulong"/>).
/// </summary>
/// <remarks>
/// Because the underlying type is <see cref="ulong"/>, every representable value is non-negative; the type
/// therefore cannot hold an invalid amount. <see cref="Zero"/> is provided for convenience.
/// </remarks>
public sealed class Experience : ValueObject
{
 /// <summary>An experience amount of zero.</summary>
    public static readonly Experience Zero = new(0UL);

 /// <summary>The experience amount.</summary>
    public ulong Value { get; }

 /// <summary>Initializes a new <see cref="Experience"/>.</summary>
 /// <param name="value">The experience amount (any non-negative 64-bit value).</param>
    public Experience(ulong value)
    {
        Value = value;
    }

 /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

 /// <summary>Returns the experience amount as its underlying <see cref="ulong"/> value.</summary>
    public override string ToString() => Value.ToString();
}
