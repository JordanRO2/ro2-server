namespace RO2.BuildingBlocks.Domain;

/// <summary>
/// The persistence contract for an aggregate. A repository works only in terms of <b>whole aggregates</b>
/// addressed by their identity — it is the single seam between the domain model and storage.
/// </summary>
/// <typeparam name="TAggregate">The aggregate-root type managed by this repository.</typeparam>
/// <typeparam name="TId">The aggregate's identifier type.</typeparam>
/// <remarks>
/// <para>
/// Implementations live per service / per infrastructure and are expected to drain the aggregate's
/// <see cref="AggregateRoot{TId}.DomainEvents"/> to the outbox in the same atomic write that persists the
/// aggregate's state, then clear them via <see cref="AggregateRoot{TId}.ClearDomainEvents"/>.
/// </para>
/// </remarks>
public interface IRepository<TAggregate, TId>
    where TAggregate : AggregateRoot<TId>
    where TId : struct
{
 /// <summary>Loads the aggregate with the given identity.</summary>
 /// <param name="id">The identity of the aggregate to load.</param>
 /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
 /// <returns>
 /// The aggregate, or <see langword="null"/> if no aggregate with the given identity exists.
 /// </returns>
    Task<TAggregate?> GetByIdAsync(TId id, CancellationToken cancellationToken = default);

 /// <summary>Adds a new aggregate to the store.</summary>
 /// <param name="aggregate">The aggregate to add.</param>
 /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
 /// <returns>A task that completes when the aggregate has been added.</returns>
    Task AddAsync(TAggregate aggregate, CancellationToken cancellationToken = default);

 /// <summary>Persists changes made to an existing aggregate.</summary>
 /// <param name="aggregate">The aggregate whose state should be persisted.</param>
 /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
 /// <returns>A task that completes when the aggregate's changes have been persisted.</returns>
    Task UpdateAsync(TAggregate aggregate, CancellationToken cancellationToken = default);
}
