using RO2.Characters.Domain;
using RO2.Server.Chat;
using RO2.Socials.Domain;
using RO2.Tests.Economy;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Audit [20]: the atomic guard counts raw stored ids, the pre-check and the visible list count live characters only.
/// A list padded with deleted characters must reclaim those slots when the guard answers ListFull.
/// </summary>
public class FriendListDeadSlotsTests
{
    private sealed class MemoryFriends : IFriendListRepository
    {
        public readonly List<uint> Ids = [];
        public Task<IReadOnlyList<uint>> LoadAsync(uint ownerId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<uint>>([.. Ids]);
        public Task<AddOutcome> AddAsync(uint ownerId, uint friendId)
        {
            if (Ids.Contains(friendId)) return Task.FromResult(AddOutcome.AlreadyFriend);
            if (Ids.Count >= SocialRules.MaxFriends) return Task.FromResult(AddOutcome.ListFull);
            Ids.Add(friendId);
            return Task.FromResult(AddOutcome.Added);
        }
        public Task RemoveAsync(uint ownerId, uint friendId) { Ids.Remove(friendId); return Task.CompletedTask; }
        public Task<bool> ListsFriendAsync(uint listOwnerId, uint friendId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<uint>> OwnersListingAsync(uint friendId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task PruneDead_ReclaimsDeletedSlots_SoTheGuardMatchesTheVisibleList()
    {
        var chars = new FakeCharacterRepository(new RepoScheduler());
        var lists = new MemoryFriends();
        // 199 deleted + 1 live friend: raw 200 (guard full), live 1 (pre-check passes).
        for (uint id = 1; id <= 199; id++)
        {
            chars.Seed(new Character { Id = id, Name = "Dead" + id, IsDeleted = true });
            lists.Ids.Add(id);
        }
        chars.Seed(new Character { Id = 500, Name = "Live" });
        lists.Ids.Add(500);
        chars.Seed(new Character { Id = 501, Name = "New" });
        var store = new FriendStore(lists, chars, null!);

        Assert.Equal(1, await store.CountAsync(7, default));
        Assert.Equal(AddOutcome.ListFull, await lists.AddAsync(7, 501));

        Assert.Equal(199, await store.PruneDeadAsync(7, default));

        Assert.Equal(new uint[] { 500 }, lists.Ids);
        Assert.Equal(AddOutcome.Added, await lists.AddAsync(7, 501));
        Assert.Equal(0, await store.PruneDeadAsync(7, default));
    }
}
