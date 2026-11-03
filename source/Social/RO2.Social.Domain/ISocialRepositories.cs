namespace RO2.Socials.Domain;

/// <summary>
/// Raw persistence of the messenger friend lists (one <see cref="FriendListDocument"/> per owner character).
/// Takes and returns only ids and outcomes; joins with the character directory or presence stay in the host.
/// </summary>
public interface IFriendListRepository
{
    /// <summary>The owner's stored friend ids in the order they were added; empty when there is no list.</summary>
    Task<IReadOnlyList<uint>> LoadAsync(uint ownerId, CancellationToken ct = default);

    /// <summary>
    /// Adds <paramref name="friendId"/>, size-guarded at <see cref="SocialRules.MaxFriends"/> in a single
    /// conditional write so concurrent adds cannot push the list past the cap.
    /// </summary>
    Task<AddOutcome> AddAsync(uint ownerId, uint friendId);

    /// <summary>Removes <paramref name="friendId"/> from the owner's list (a no-op when it is not there).</summary>
    Task RemoveAsync(uint ownerId, uint friendId);

    /// <summary>Whether <paramref name="listOwnerId"/> has <paramref name="friendId"/> in its stored list.</summary>
    Task<bool> ListsFriendAsync(uint listOwnerId, uint friendId, CancellationToken ct = default);

    /// <summary>The ids of the owners whose stored list contains <paramref name="friendId"/>.</summary>
    Task<IReadOnlyList<uint>> OwnersListingAsync(uint friendId, CancellationToken ct = default);
}

/// <summary>
/// Raw persistence of the messenger block lists (one <see cref="BlockListDocument"/> per owner character).
/// The name refresh against the character directory stays in the host.
/// </summary>
public interface IBlockListRepository
{
    /// <summary>The owner's stored entries exactly as persisted (no name refresh); empty when there is no list.</summary>
    Task<IReadOnlyList<BlockListEntry>> GetStoredEntriesAsync(uint ownerId, CancellationToken ct = default);

    /// <summary>
    /// Adds <paramref name="characterId"/> in a single conditional write: not already present and below
    /// <see cref="SocialRules.MaxBlocked"/>.
    /// </summary>
    Task<BlockAddOutcome> AddAsync(uint ownerId, uint characterId, string name, CancellationToken ct = default);

    /// <summary>Whether <paramref name="ownerId"/> blocks <paramref name="characterId"/>.</summary>
    Task<bool> BlocksAsync(uint ownerId, uint characterId, CancellationToken ct = default);

    /// <summary>Removes the character that <paramref name="uid"/> names (its low dword) from the owner's list.</summary>
    Task RemoveAsync(uint ownerId, ulong uid, CancellationToken ct = default);

    /// <summary>The subset of <paramref name="ownerIds"/> whose block list holds <paramref name="characterId"/>.</summary>
    Task<IReadOnlySet<uint>> OwnersBlockingAsync(IReadOnlyCollection<uint> ownerIds, uint characterId, CancellationToken ct = default);
}
