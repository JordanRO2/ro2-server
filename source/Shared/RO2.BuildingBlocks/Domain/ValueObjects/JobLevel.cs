namespace RO2.BuildingBlocks.Domain.ValueObjects;

/// <summary>
/// A character job level: an integer of at least <see cref="MinValue"/>. A newly created character starts
/// at job level <c>1</c> (mirroring <c>Character.JobLevel</c>'s default).
/// </summary>
public sealed class JobLevel : ValueObject
{
 /// <summary>The lowest valid job level (the job level of a newly created character).</summary>
    public const int MinValue = 1;

 /// <summary>The job level value, guaranteed to be at least <see cref="MinValue"/>.</summary>
    public int Value { get; }

 /// <summary>Initializes a new <see cref="JobLevel"/>.</summary>
 /// <param name="value">The job level; must be greater than or equal to <see cref="MinValue"/>.</param>
 /// <exception cref="ArgumentOutOfRangeException">
 /// Thrown when <paramref name="value"/> is below <see cref="MinValue"/>.
 /// </exception>
    public JobLevel(int value)
    {
        if (value < MinValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"Job level must be greater than or equal to {MinValue}.");
        }

        Value = value;
    }

 /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

 /// <summary>Returns the job level as its underlying <see cref="int"/> value.</summary>
    public override string ToString() => Value.ToString();
}
