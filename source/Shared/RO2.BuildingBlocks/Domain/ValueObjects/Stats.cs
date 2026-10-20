namespace RO2.BuildingBlocks.Domain.ValueObjects;

/// <summary>
/// The six primary character attributes (STR, AGI, VIT, INT, DEX, LUK), each a non-negative integer.
/// Mirrors the <c>int</c> stat fields on <c>Character</c>.
/// </summary>
/// <remarks>
/// Each attribute must be non-negative; negative attribute values are not representable in game and would
/// corrupt downstream combat/derived-stat calculations.
/// </remarks>
public sealed class Stats : ValueObject
{
 /// <summary>The smallest valid value for any single attribute.</summary>
    public const int MinValue = 0;

 /// <summary>Strength.</summary>
    public int Str { get; }

 /// <summary>Agility.</summary>
    public int Agi { get; }

 /// <summary>Vitality.</summary>
    public int Vit { get; }

 /// <summary>Intelligence.</summary>
    public int Int { get; }

 /// <summary>Wisdom (RO2:LotS "WIS"; was the legacy "Dex").</summary>
    public int Wis { get; }

 /// <summary>Luck.</summary>
    public int Luk { get; }

 /// <summary>Initializes a new <see cref="Stats"/>.</summary>
 /// <param name="str">Strength; must be non-negative.</param>
 /// <param name="agi">Agility; must be non-negative.</param>
 /// <param name="vit">Vitality; must be non-negative.</param>
 /// <param name="int">Intelligence; must be non-negative.</param>
 /// <param name="wis">Wisdom; must be non-negative.</param>
 /// <param name="luk">Luck; must be non-negative.</param>
 /// <exception cref="ArgumentOutOfRangeException">Thrown when any attribute is negative.</exception>
    public Stats(int str, int agi, int vit, int @int, int wis, int luk)
    {
        ThrowIfNegative(str, nameof(str));
        ThrowIfNegative(agi, nameof(agi));
        ThrowIfNegative(vit, nameof(vit));
        ThrowIfNegative(@int, nameof(@int));
        ThrowIfNegative(wis, nameof(wis));
        ThrowIfNegative(luk, nameof(luk));

        Str = str;
        Agi = agi;
        Vit = vit;
        Int = @int;
        Wis = wis;
        Luk = luk;
    }

    private static void ThrowIfNegative(int value, string paramName)
    {
        if (value < MinValue)
        {
            throw new ArgumentOutOfRangeException(
                paramName, value, $"Attribute values must be greater than or equal to {MinValue}.");
        }
    }

 /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Str;
        yield return Agi;
        yield return Vit;
        yield return Int;
        yield return Wis;
        yield return Luk;
    }

 /// <summary>Returns the attributes formatted as <c>STR/AGI/VIT/INT/WIS/LUK</c>.</summary>
    public override string ToString() => $"STR {Str}/AGI {Agi}/VIT {Vit}/INT {Int}/WIS {Wis}/LUK {Luk}";
}
