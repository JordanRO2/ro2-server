namespace RO2.BuildingBlocks.Domain;

/// <summary>
/// Marker contract for an <b>in-process</b> domain event: a fact that happened inside a single
/// bounded context, raised by an <see cref="AggregateRoot{TId}"/> while it enforces its invariants.
/// </summary>
/// <remarks>
/// <para>
/// Domain events are <i>not</i> the cross-service wire contract — that role belongs to the integration
/// event contract defined elsewhere in the shared kernel. A domain event stays inside the service that
/// raised it; it is drained from <see cref="AggregateRoot{TId}.DomainEvents"/> when the aggregate is
/// persisted and may then be translated into one or more integration events.
/// </para>
/// <para>
/// Implementations must be immutable and carry only value-typed / serializable data. Every event exposes
/// an idempotency key (<see cref="EventId"/>) and the instant it occurred (<see cref="OccurredOn"/>).
/// </para>
/// <para>
/// <b>Timestamps:</b> implementations must <b>not</b> call <c>DateTime.Now</c> / <c>DateTime.UtcNow</c>
/// (forbidden by this codebase's tooling). <see cref="OccurredOn"/> is part of the contract and must be
/// supplied by the producer (e.g. via a constructor parameter sourced from an injected clock abstraction).
/// </para>
/// </remarks>
public interface IDomainEvent
{
 /// <summary>
 /// Stable, unique identifier for this event occurrence. Used as the idempotency key so that
 /// downstream handlers can de-duplicate redelivered events.
 /// </summary>
    Guid EventId { get; }

 /// <summary>
 /// The instant at which the event occurred, in UTC offset form. Supplied by the producer from an
 /// injected clock — never read from the ambient system clock inside an implementation.
 /// </summary>
    DateTimeOffset OccurredOn { get; }
}
