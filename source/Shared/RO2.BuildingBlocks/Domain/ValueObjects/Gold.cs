namespace RO2.BuildingBlocks.Domain.ValueObjects;

/// <summary>
/// An amount of in-game currency (zeny/gold): a non-negative 64-bit value. Mirrors the <see cref="long"/>
/// <c>Character.Gold</c> field while forbidding negative balances.
/// </summary>
public sealed class Gold : ValueObject
{
 /// <summary>The smallest valid balance.</summary>
    public const long MinValue = 0;

 /// <summary>A zero balance.</summary>
    public static readonly Gold Zero = new(0L);

 /// <summary>The balance, guaranteed to be at least <see cref="MinValue"/>.</summary>
    public long Value { get; }

 /// <summary>Initializes a new <see cref="Gold"/>.</summary>
 /// <param name="value">The balance; must be greater than or equal to <see cref="MinValue"/>.</param>
 /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="value"/> is negative.</exception>
    public Gold(long value)
    {
        if (value < MinValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"Gold must be greater than or equal to {MinValue}.");
        }

        Value = value;
    }

 /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

 /// <summary>Returns the balance as its underlying <see cref="long"/> value.</summary>
    public override string ToString() => Value.ToString();
}
