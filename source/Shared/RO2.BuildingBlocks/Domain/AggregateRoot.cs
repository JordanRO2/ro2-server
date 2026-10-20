namespace RO2.BuildingBlocks.Domain;

/// <summary>
/// Base class for an aggregate root: the <b>only</b> transactional and domain-event boundary in a model.
/// External code mutates the aggregate solely through its public methods, which enforce invariants and
/// record <see cref="IDomainEvent"/>s via <see cref="Raise(IDomainEvent)"/>.
/// </summary>
/// <typeparam name="TId">The value type used as the aggregate's identifier.</typeparam>
/// <remarks>
/// <para>
/// Child entities and value objects inside the aggregate are reachable only through the root and must be
/// mutated through it, so the root can guarantee the aggregate's consistency as a single unit.
/// </para>
/// <para>
/// Raised events accumulate in <see cref="DomainEvents"/> until the aggregate is persisted. The
/// persistence layer is expected to drain them (typically into a transactional outbox in the same atomic
/// write) and then call <see cref="ClearDomainEvents"/>.
/// </para>
/// </remarks>
public abstract class AggregateRoot<TId> : Entity<TId>
    where TId : struct
{
    private readonly List<IDomainEvent> _domainEvents = [];

 /// <summary>
 /// Monotonic version of the aggregate, used for optimistic-concurrency checks and as an ordering
 /// sequence for the events it produces. Advanced by the persistence layer / aggregate behavior.
 /// </summary>
    public long Version { get; protected set; }

 /// <summary>
 /// The domain events recorded by this aggregate since it was loaded or since the events were last
 /// cleared, in the order they were raised. Exposed as a read-only snapshot to callers.
 /// </summary>
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

 /// <summary>
 /// Records a domain event to be published once the aggregate is successfully persisted. Intended to
 /// be called from within the aggregate's own invariant-enforcing methods.
 /// </summary>
 /// <param name="domainEvent">The event to record. Must not be <see langword="null"/>.</param>
 /// <exception cref="ArgumentNullException">Thrown when <paramref name="domainEvent"/> is <see langword="null"/>.</exception>
    protected void Raise(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _domainEvents.Add(domainEvent);
    }

 /// <summary>
 /// Clears all recorded domain events. Called by the persistence layer after the events have been
 /// durably handed off (e.g. written to the outbox).
 /// </summary>
    public void ClearDomainEvents() => _domainEvents.Clear();
}
