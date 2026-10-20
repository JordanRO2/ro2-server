namespace RO2.BuildingBlocks.Domain.ValueObjects;

/// <summary>
/// A character base level: an integer between <see cref="MinValue"/> and <see cref="MaxValue"/> inclusive.
/// </summary>
/// <remarks>
/// The level cap of <c>50</c> mirrors the live combat rules (see <c>CombatService.GrantExpAsync</c>, which
/// stops granting level-ups once <c>player.Level &lt; 50</c> is no longer satisfied). A freshly created
/// character starts at level <c>1</c>.
/// </remarks>
public sealed class Level : ValueObject
{
 /// <summary>The lowest valid level (the level of a newly created character).</summary>
    public const int MinValue = 1;

 /// <summary>The highest valid level (the current level cap).</summary>
    public const int MaxValue = 50;

 /// <summary>The level value, guaranteed to be within <see cref="MinValue"/>..<see cref="MaxValue"/>.</summary>
    public int Value { get; }

 /// <summary>Initializes a new <see cref="Level"/>.</summary>
 /// <param name="value">The level value; must be between <see cref="MinValue"/> and <see cref="MaxValue"/> inclusive.</param>
 /// <exception cref="ArgumentOutOfRangeException">
 /// Thrown when <paramref name="value"/> is below <see cref="MinValue"/> or above <see cref="MaxValue"/>.
 /// </exception>
    public Level(int value)
    {
        if (value is < MinValue or > MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"Level must be between {MinValue} and {MaxValue} inclusive.");
        }

        Value = value;
    }

 /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

 /// <summary>Returns the level as its underlying <see cref="int"/> value.</summary>
    public override string ToString() => Value.ToString();
}
