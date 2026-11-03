using RO2.Socials.Application;
using RO2.Socials.Domain;
using Xunit;

namespace RO2.Tests.Packets;

// B14 S8: the add-friend / add-block decisions from the repository write onward (23508 result codes, 23510 refusal, 23512 notify-gate).
public class AddFriendBlockUseCaseTests
{
    private sealed class FakeFriends(AddOutcome outcome, bool targetListsRequester) : IFriendListRepository
    {
        public int ListsCalls;
        public Task<IReadOnlyList<uint>> LoadAsync(uint ownerId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AddOutcome> AddAsync(uint ownerId, uint friendId) => Task.FromResult(outcome);
        public Task RemoveAsync(uint ownerId, uint friendId) => throw new NotSupportedException();
        public Task<bool> ListsFriendAsync(uint listOwnerId, uint friendId, CancellationToken ct = default)
        { ListsCalls++; return Task.FromResult(targetListsRequester); }
        public Task<IReadOnlyList<uint>> OwnersListingAsync(uint friendId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeBlocks(BlockAddOutcome outcome, bool blocks) : IBlockListRepository
    {
        public Task<IReadOnlyList<BlockListEntry>> GetStoredEntriesAsync(uint ownerId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BlockAddOutcome> AddAsync(uint ownerId, uint characterId, string name, CancellationToken ct = default) => Task.FromResult(outcome);
        public Task<bool> BlocksAsync(uint ownerId, uint characterId, CancellationToken ct = default) => Task.FromResult(blocks);
        public Task RemoveAsync(uint ownerId, ulong uid, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlySet<uint>> OwnersBlockingAsync(IReadOnlyCollection<uint> ownerIds, uint characterId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static Task<AddFriendResult> Friend(AddOutcome o, bool hasName, bool lists, bool blocks) =>
        new AddFriendUseCase(new FakeFriends(o, lists), new FakeBlocks(BlockAddOutcome.Added, blocks)).ExecuteAsync(1, 2, hasName);

    [Fact]
    public async Task AddFriend_maps_AlreadyFriend_to_6_and_ListFull_to_33_without_notice()
    {
        Assert.Equal(new AddFriendResult(6, false, false), await Friend(AddOutcome.AlreadyFriend, true, false, false));
        Assert.Equal(new AddFriendResult(33, false, false), await Friend(AddOutcome.ListFull, true, false, false));
    }

    [Fact]
    public async Task AddFriend_added_notifies_only_with_a_name_and_no_add_back_and_no_block()
    {
        Assert.Equal(new AddFriendResult(0, true, true), await Friend(AddOutcome.Added, true, false, false));
        Assert.Equal(new AddFriendResult(0, true, false), await Friend(AddOutcome.Added, false, false, false));
        Assert.Equal(new AddFriendResult(0, true, false, TargetListsRequester: true), await Friend(AddOutcome.Added, true, true, true));
        Assert.Equal(new AddFriendResult(0, true, false, TargetBlocksRequester: true), await Friend(AddOutcome.Added, true, false, true));
    }

    [Fact]
    public async Task AddFriend_without_a_name_does_not_read_the_target_list()
    {
        var friends = new FakeFriends(AddOutcome.Added, false);
        await new AddFriendUseCase(friends, new FakeBlocks(BlockAddOutcome.Added, false)).ExecuteAsync(1, 2, false);
        Assert.Equal(0, friends.ListsCalls);
    }

    [Theory]
    [InlineData(BlockAddOutcome.Added, null)]
    [InlineData(BlockAddOutcome.AlreadyRegistered, (ushort)7)]
    [InlineData(BlockAddOutcome.ListFull, null)]
    public async Task AddBlock_refuses_only_an_already_registered_name_with_7(BlockAddOutcome outcome, ushort? refusal)
    {
        var r = await new AddBlockUseCase(new FakeBlocks(outcome, false)).ExecuteAsync(1, 2, "x");
        Assert.Equal(new AddBlockResult(outcome, refusal), r);
        Assert.Equal(outcome == BlockAddOutcome.Added, r.Added);
    }
}
