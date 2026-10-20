namespace RO2.BuildingBlocks.Bus;

/// <summary>
/// Abstraction over the asynchronous message bus used to publish and subscribe to
/// <see cref="IIntegrationEvent"/>s across bounded contexts.
/// </summary>
/// <remarks>
/// The production implementation targets NATS (Core for volatile real-time fan-out, JetStream for
/// durable, replayable settlement/outbox traffic). Subject naming, queue groups, retry and DLQ policy
/// live in the concrete implementation; this contract stays transport-agnostic so domain/application
/// code never depends on the broker.
/// </remarks>
public interface IEventBus
{
 /// <summary>
 /// Publishes an integration event onto the bus.
 /// </summary>
 /// <typeparam name="T">The concrete integration-event type.</typeparam>
 /// <param name="evt">The event instance to publish.</param>
 /// <param name="cancellationToken">A token to cancel the publish operation.</param>
 /// <returns>A task that completes once the event has been handed to the transport.</returns>
    Task PublishAsync<T>(T evt, CancellationToken cancellationToken = default)
        where T : IIntegrationEvent;

 /// <summary>
 /// Subscribes a handler to every integration event of type <typeparamref name="T"/>.
 /// </summary>
 /// <typeparam name="T">The concrete integration-event type to receive.</typeparam>
 /// <param name="handler">
 /// The asynchronous handler invoked for each delivered event. Handlers must be idempotent
 /// (use <see cref="IIntegrationEvent.EventId"/> for de-duplication) because delivery is at-least-once.
 /// </param>
 /// <param name="cancellationToken">A token to cancel establishing the subscription.</param>
 /// <returns>A task that completes once the subscription is registered.</returns>
    Task SubscribeAsync<T>(Func<T, Task> handler, CancellationToken cancellationToken = default)
        where T : IIntegrationEvent;
}
