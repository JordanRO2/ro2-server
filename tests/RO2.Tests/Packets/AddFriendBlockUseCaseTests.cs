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

    private static AddFriendUseCase Make(AddOutcome o, bool lists, bool blocks, out FakeFriends friends)
        => new(friends = new FakeFriends(o, lists), new FakeBlocks(BlockAddOutcome.Added, blocks));

    [Theory]
    [InlineData(AddOutcome.AlreadyFriend, (ushort)6, false)]
    [InlineData(AddOutcome.ListFull, (ushort)33, false)]
    [InlineData(AddOutcome.Added, (ushort)0, true)]
    public async Task AddFriend_maps_the_outcome_to_the_23508_code_without_reading_the_gate(AddOutcome o, ushort code, bool added)
    {
        var uc = Make(o, true, true, out var friends);
        Assert.Equal(new AddFriendResult(code, added), await uc.ExecuteAsync(1, 2));
        Assert.Equal(0, friends.ListsCalls);
    }

    [Fact]
    public async Task Gate_notifies_only_with_a_name_and_no_add_back_and_no_block()
    {
        Assert.Equal(new FriendNoticeDecision(true), await Make(AddOutcome.Added, false, false, out _).ShouldNotifyAsync(1, 2, true));
        Assert.Equal(new FriendNoticeDecision(false), await Make(AddOutcome.Added, false, false, out _).ShouldNotifyAsync(1, 2, false));
        Assert.Equal(new FriendNoticeDecision(false, TargetListsRequester: true), await Make(AddOutcome.Added, true, true, out _).ShouldNotifyAsync(1, 2, true));
        Assert.Equal(new FriendNoticeDecision(false, TargetBlocksRequester: true), await Make(AddOutcome.Added, false, true, out _).ShouldNotifyAsync(1, 2, true));
    }

    [Fact]
    public async Task Gate_without_a_name_does_not_read_the_target_list()
    {
        var uc = Make(AddOutcome.Added, false, false, out var friends);
        await uc.ShouldNotifyAsync(1, 2, false);
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
