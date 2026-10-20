namespace RO2.BuildingBlocks.Bus;

/// <summary>
/// Base contract for an <b>integration event</b>: a transport-agnostic, cross-service fact published
/// onto the message bus <i>after</i> an aggregate has committed (via the outbox). Unlike an in-process
/// <c>IDomainEvent</c>, an integration event is a stable, versioned contract that leaves a bounded
/// context and is consumed by gateways and by other services.
/// </summary>
/// <remarks>
/// <para>
/// Implementations must be immutable and serializable, carrying only semantic value-typed data.
/// <b>Never</b> carry raw wire bytes or transport framing; those details stay inside the gateways.
/// </para>
/// <para>
/// <b>Versioning (additive-only):</b> new fields are added as optional/nullable with <b>no</b>
/// <see cref="SchemaVersion"/> bump; old subscribers ignore unknown fields. <see cref="SchemaVersion"/>
/// bumps only on a breaking change (rename/remove/retype), which publishes to a new subject suffix
/// (e.g. <c>.v2</c>) alongside the old one. Fields whose meaning is not yet confirmed are prefixed
/// <c>Tentative_</c>; consumers must tolerate them being absent or zero.
/// </para>
/// <para>
/// <b>Timestamps:</b> implementations must <b>not</b> call <c>DateTime.Now</c> / <c>DateTime.UtcNow</c>
/// (forbidden by this codebase's tooling). Any timestamp is supplied by the producer and passed in.
/// </para>
/// </remarks>
public interface IIntegrationEvent
{
 /// <summary>
 /// Stable, unique identifier for this event occurrence. Used as the idempotency key so that
 /// at-least-once delivery (JetStream) cannot cause a consumer to process the same event twice.
 /// </summary>
    Guid EventId { get; }

 /// <summary>
 /// The contract's schema version. Bumps <b>only</b> on a breaking change; additive new fields do
 /// not change it.
 /// </summary>
    int SchemaVersion { get; }
}
