namespace RO2.BuildingBlocks.Domain.ValueObjects;

/// <summary>
/// A depletable vital resource pool used for HP and MP: a <see cref="Current"/> amount bounded by a
/// <see cref="Max"/> capacity. Mirrors the <c>Hp</c>/<c>MaxHp</c> and <c>Mp</c>/<c>MaxMp</c> integer pairs
/// on <c>Character</c>.
/// </summary>
/// <remarks>
/// The invariant is <c>0 &lt;= Current &lt;= Max</c>: the pool can never be negative and can never exceed its
/// capacity. A <see cref="Max"/> of <c>0</c> with a <see cref="Current"/> of <c>0</c> is permitted.
/// </remarks>
public sealed class Vital : ValueObject
{
 /// <summary>The current amount, always in the range <c>0..</c><see cref="Max"/>.</summary>
    public int Current { get; }

 /// <summary>The maximum capacity, always non-negative.</summary>
    public int Max { get; }

 /// <summary>Initializes a new <see cref="Vital"/>.</summary>
 /// <param name="current">The current amount; must be non-negative and not exceed <paramref name="max"/>.</param>
 /// <param name="max">The maximum capacity; must be non-negative.</param>
 /// <exception cref="ArgumentOutOfRangeException">
 /// Thrown when <paramref name="max"/> is negative, when <paramref name="current"/> is negative, or when
 /// <paramref name="current"/> exceeds <paramref name="max"/>.
 /// </exception>
    public Vital(int current, int max)
    {
        if (max < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(max), max, "Max must be non-negative.");
        }

        if (current < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(current), current, "Current must be non-negative.");
        }

        if (current > max)
        {
            throw new ArgumentOutOfRangeException(
                nameof(current), current, $"Current ({current}) must not exceed Max ({max}).");
        }

        Current = current;
        Max = max;
    }

 /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Current;
        yield return Max;
    }

 /// <summary>Returns the pool formatted as <c>Current/Max</c>.</summary>
    public override string ToString() => $"{Current}/{Max}";
}
