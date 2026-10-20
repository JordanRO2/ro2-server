namespace RO2.BuildingBlocks.Domain.ValueObjects;

/// <summary>
/// A character master level: a non-negative integer. Defaults to <c>0</c> for a character that has not
/// yet reached the master-level progression.
/// </summary>
/// <remarks>
/// The persisted form on <c>Character.MasterLevel</c> is a <see cref="ushort"/>; this value object accepts
/// any non-negative <see cref="int"/> and enforces only the lower bound.
/// </remarks>
public sealed class MasterLevel : ValueObject
{
 /// <summary>The lowest valid master level.</summary>
    public const int MinValue = 0;

 /// <summary>The master level value, guaranteed to be at least <see cref="MinValue"/>.</summary>
    public int Value { get; }

 /// <summary>Initializes a new <see cref="MasterLevel"/>.</summary>
 /// <param name="value">The master level; must be greater than or equal to <see cref="MinValue"/>.</param>
 /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="value"/> is negative.</exception>
    public MasterLevel(int value)
    {
        if (value < MinValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"Master level must be greater than or equal to {MinValue}.");
        }

        Value = value;
    }

 /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

 /// <summary>Returns the master level as its underlying <see cref="int"/> value.</summary>
    public override string ToString() => Value.ToString();
}
