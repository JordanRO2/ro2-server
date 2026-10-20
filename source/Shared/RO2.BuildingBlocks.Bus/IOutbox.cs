namespace RO2.BuildingBlocks.Bus;

/// <summary>
/// Contract for the transactional <b>outbox</b>: integration events are written to the owning service's
/// own store <i>in the same atomic write</i> as the aggregate state change, then a relay publishes them
/// to the bus. This is the only correct "state changed ⇒ event emitted" guarantee without a 2PC across
/// the database and the broker.
/// </summary>
/// <remarks>
/// <para>
/// The repository's <c>SaveAsync</c>-style persistence drains an aggregate's domain
/// events into the outbox as integration events in that same write; a separate relay process then reads
/// pending rows, publishes them via <see cref="IEventBus"/>, and marks them sent. This interface is the
/// contract only — concrete storage (e.g. a Mongo <c>outbox</c> collection) and the relay live per service
/// in the infrastructure layer.
/// </para>
/// </remarks>
public interface IOutbox
{
 /// <summary>
 /// Stages an integration event for later publication. Implementations must enlist this write in the
 /// same transaction/atomic operation that persists the originating aggregate state.
 /// </summary>
 /// <typeparam name="T">The concrete integration-event type.</typeparam>
 /// <param name="evt">The event to stage for publication.</param>
 /// <param name="cancellationToken">A token to cancel the operation.</param>
 /// <returns>A task that completes once the event has been staged.</returns>
    Task EnqueueAsync<T>(T evt, CancellationToken cancellationToken = default)
        where T : IIntegrationEvent;
}
