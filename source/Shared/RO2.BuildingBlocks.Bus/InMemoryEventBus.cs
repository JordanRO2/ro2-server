using System.Collections.Concurrent;

namespace RO2.BuildingBlocks.Bus;

/// <summary>
/// In-process, development-only <see cref="IEventBus"/> implementation. Publishing an event invokes every
/// handler subscribed to that exact event type, in-process, on the caller's continuation.
/// </summary>
/// <remarks>
/// <para>
/// This exists so the bus seam can be wired and unit-tested without standing up a broker. It provides
/// <b>no</b> durability, <b>no</b> replay, <b>no</b> cross-process delivery, and <b>no</b> per-aggregate
/// ordering guarantees — do not use it in production.
/// </para>
/// <para>
/// The production bus is planned on NATS: Core NATS for volatile real-time fan-out and JetStream for
/// durable, at-least-once, replayable command, event and outbox traffic, with subject naming, queue groups,
/// per-aggregate partitioning, and retry and dead-letter policy. It will replace this stub at the
/// composition root.
/// </para>
/// </remarks>
public sealed class InMemoryEventBus : IEventBus
{
    private readonly ConcurrentDictionary<Type, List<Func<object, Task>>> _handlers = new();

 /// <inheritdoc />
    public async Task PublishAsync<T>(T evt, CancellationToken cancellationToken = default)
        where T : IIntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (!_handlers.TryGetValue(typeof(T), out var handlers))
        {
            return;
        }

        
        Func<object, Task>[] snapshot;
        lock (handlers)
        {
            snapshot = handlers.ToArray();
        }

        foreach (var handler in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await handler(evt).ConfigureAwait(false);
        }
    }

 /// <inheritdoc />
    public Task SubscribeAsync<T>(Func<T, Task> handler, CancellationToken cancellationToken = default)
        where T : IIntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(handler);

        var list = _handlers.GetOrAdd(typeof(T), static _ => new List<Func<object, Task>>());
        lock (list)
        {
            list.Add(evt => handler((T)evt));
        }

        return Task.CompletedTask;
    }
}
