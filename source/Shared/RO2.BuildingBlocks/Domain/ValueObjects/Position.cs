namespace RO2.BuildingBlocks.Domain.ValueObjects;

/// <summary>
/// A world position expressed as a triple of single-precision coordinates, matching the client's
/// <c>X</c>/<c>Y</c>/<c>Z</c> float layout (<c>Character.X</c>/<c>Y</c>/<c>Z</c>).
/// </summary>
/// <remarks>
/// A position does not constrain the magnitude of its coordinates (any map can place an entity anywhere in
/// world space), but non-finite coordinates (<see cref="float.NaN"/>, ±∞) are rejected because they cannot
/// represent a real location and break distance/equality math.
/// </remarks>
public sealed class Position : ValueObject
{
 /// <summary>The X (east-west) coordinate.</summary>
    public float X { get; }

 /// <summary>The Y (north-south) coordinate.</summary>
    public float Y { get; }

 /// <summary>The Z (vertical) coordinate.</summary>
    public float Z { get; }

 /// <summary>Initializes a new <see cref="Position"/>.</summary>
 /// <param name="x">The X coordinate; must be a finite number.</param>
 /// <param name="y">The Y coordinate; must be a finite number.</param>
 /// <param name="z">The Z coordinate; must be a finite number.</param>
 /// <exception cref="ArgumentOutOfRangeException">
 /// Thrown when any coordinate is <see cref="float.NaN"/> or infinite.
 /// </exception>
    public Position(float x, float y, float z)
    {
        ThrowIfNotFinite(x, nameof(x));
        ThrowIfNotFinite(y, nameof(y));
        ThrowIfNotFinite(z, nameof(z));

        X = x;
        Y = y;
        Z = z;
    }

    private static void ThrowIfNotFinite(float value, string paramName)
    {
        if (!float.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(
                paramName, value, "Position coordinates must be finite numbers.");
        }
    }

 /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return X;
        yield return Y;
        yield return Z;
    }

 /// <summary>Returns the position formatted as <c>(X, Y, Z)</c>.</summary>
    public override string ToString() => $"({X}, {Y}, {Z})";
}
