namespace RO2.BuildingBlocks.Domain;

/// <summary>
/// Base class for a value object: an immutable, identity-less concept whose equality is <b>structural</b>,
/// i.e. two value objects are equal when their constituent values are equal.
/// </summary>
/// <remarks>
/// <para>
/// Derived types override <see cref="GetEqualityComponents"/> to yield, in a stable order, the components
/// that define their value. Equality, inequality and hashing are then derived from those components, so
/// derived types need only declare what makes them equal — not how to compare.
/// </para>
/// <para>
/// Value objects must be immutable: their equality components are expected not to change over the object's
/// lifetime. Two instances of <i>different</i> runtime types are never equal, even if their components
/// happen to match.
/// </para>
/// </remarks>
public abstract class ValueObject
{
 /// <summary>
 /// Yields, in a stable order, the components that constitute this value object's value. The order and
 /// set of returned components must be deterministic for a given instance.
 /// </summary>
 /// <returns>The sequence of equality components (each may be <see langword="null"/>).</returns>
    protected abstract IEnumerable<object?> GetEqualityComponents();

 /// <summary>
 /// Determines whether the specified object is a value object of the exact same runtime type whose
 /// equality components are pairwise equal to this instance's components.
 /// </summary>
 /// <param name="obj">The object to compare with the current value object.</param>
 /// <returns>
 /// <see langword="true"/> if <paramref name="obj"/> is a <see cref="ValueObject"/> of the same runtime
 /// type with equal components; otherwise <see langword="false"/>.
 /// </returns>
    public override bool Equals(object? obj)
    {
        if (obj is not ValueObject other || GetType() != other.GetType())
        {
            return false;
        }

        return GetEqualityComponents().SequenceEqual(other.GetEqualityComponents());
    }

 /// <summary>Returns a hash code derived from the value object's equality components.</summary>
 /// <returns>A hash code combining each component (treating <see langword="null"/> components as zero).</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var component in GetEqualityComponents())
        {
            hash.Add(component);
        }

        return hash.ToHashCode();
    }

 /// <summary>Structural equality operator. See <see cref="Equals(object?)"/> for the semantics.</summary>
 /// <param name="left">The left operand.</param>
 /// <param name="right">The right operand.</param>
 /// <returns><see langword="true"/> if the operands are structurally equal; otherwise <see langword="false"/>.</returns>
    public static bool operator ==(ValueObject? left, ValueObject? right)
        => left is null ? right is null : left.Equals(right);

 /// <summary>Structural inequality operator. The negation of <see cref="operator ==(ValueObject, ValueObject)"/>.</summary>
 /// <param name="left">The left operand.</param>
 /// <param name="right">The right operand.</param>
 /// <returns><see langword="true"/> if the operands are not structurally equal; otherwise <see langword="false"/>.</returns>
    public static bool operator !=(ValueObject? left, ValueObject? right) => !(left == right);
}
