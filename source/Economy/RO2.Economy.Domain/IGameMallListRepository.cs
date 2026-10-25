namespace RO2.Economy.Domain;

/// <summary>
/// Per-ACCOUNT storage of game-mall item list 2, the list 1597 ReqGameMallUpdate adds to and 1593
/// ReqGameMallItemList (list byte 2) reads back as 2865. Per account, not per character; the stored list is
/// newest-first.
/// </summary>
/// <remarks>
/// The 2865 that answers a 1593 after an acknowledged 1597 carries the added id IN FRONT of the ids it had
/// before, kept per account across characters and relogs. No duplicates: the client drops ids already in its
/// list-2 map before sending 1597, and <see cref="AddAsync"/> skips a known id too, so a stale or modified
/// client cannot grow the list with repeats.
/// </remarks>
public interface IGameMallListRepository
{
 /// <summary>The account's list, newest first; empty when it never added an id.</summary>
    Task<IReadOnlyList<uint>> GetAsync(uint accountId, CancellationToken ct = default);

 /// <summary>
 /// Put <paramref name="ids"/> in front of the account's list (one at a time, skipping ids already in it)
 /// and return the list as stored.
 /// </summary>
    Task<IReadOnlyList<uint>> AddAsync(uint accountId, IReadOnlyList<uint> ids, CancellationToken ct = default);
}
