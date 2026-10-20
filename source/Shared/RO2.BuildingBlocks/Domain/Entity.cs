namespace RO2.BuildingBlocks.Domain;

/// <summary>
/// Base class for a domain entity: an object whose equality is defined by its <b>identity</b>
/// (<see cref="Id"/>) rather than by its attribute values.
/// </summary>
/// <typeparam name="TId">
/// The value type used as the entity's identifier (e.g. a strongly-typed id <c>struct</c>,
/// a <see cref="Guid"/>, or an integral type). Constrained to <c>struct</c> so identity is a value.
/// </typeparam>
/// <remarks>
/// <para>
/// Two entities are considered equal if, and only if, they are of the exact same runtime type and share
/// the same non-default <see cref="Id"/>. Entities with a default (unsaved / transient) identifier are
/// never equal to one another, since the default value carries no identity.
/// </para>
/// <para>
/// The setter on <see cref="Id"/> is <c>protected init</c>, so identity is assigned at construction by
/// the derived type and is immutable thereafter.
/// </para>
/// </remarks>
public abstract class Entity<TId>
    where TId : struct
{
 /// <summary>
 /// The identity of this entity. Assigned once at construction by the derived type and immutable
 /// afterwards. A value equal to <c>default(TId)</c> denotes a transient (not-yet-persisted) entity.
 /// </summary>
    public TId Id { get; protected init; }

 /// <summary>
 /// Determines whether the specified object is an entity of the exact same type sharing this entity's
 /// non-default <see cref="Id"/>.
 /// </summary>
 /// <param name="obj">The object to compare with the current entity.</param>
 /// <returns>
 /// <see langword="true"/> if <paramref name="obj"/> is an <see cref="Entity{TId}"/> of the same
 /// runtime type, neither identifier is the default value, and the identifiers are equal; otherwise
 /// <see langword="false"/>.
 /// </returns>
    public override bool Equals(object? obj)
    {
        if (obj is not Entity<TId> other)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (GetType() != other.GetType())
        {
            return false;
        }

        
        if (EqualityComparer<TId>.Default.Equals(Id, default) ||
            EqualityComparer<TId>.Default.Equals(other.Id, default))
        {
            return false;
        }

        return EqualityComparer<TId>.Default.Equals(Id, other.Id);
    }

 /// <summary>Returns a hash code derived from the runtime type and the identity.</summary>
 /// <returns>A hash code combining <see cref="object.GetType"/> and <see cref="Id"/>.</returns>
    public override int GetHashCode() => HashCode.Combine(GetType(), Id);

 /// <summary>Identity equality operator. See <see cref="Equals(object?)"/> for the semantics.</summary>
 /// <param name="left">The left operand.</param>
 /// <param name="right">The right operand.</param>
 /// <returns><see langword="true"/> if the operands are equal entities; otherwise <see langword="false"/>.</returns>
    public static bool operator ==(Entity<TId>? left, Entity<TId>? right)
        => left is null ? right is null : left.Equals(right);

 /// <summary>Identity inequality operator. The negation of <see cref="operator ==(Entity{TId}, Entity{TId})"/>.</summary>
 /// <param name="left">The left operand.</param>
 /// <param name="right">The right operand.</param>
 /// <returns><see langword="true"/> if the operands are not equal entities; otherwise <see langword="false"/>.</returns>
    public static bool operator !=(Entity<TId>? left, Entity<TId>? right) => !(left == right);
}
