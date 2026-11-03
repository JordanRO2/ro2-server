using RO2.Socials.Domain;

namespace RO2.Socials.Application;

/// <summary>
/// What an add-friend decided: the 23508 result code, whether the friend was added, and whether the added player gets
/// a 23512 NfyAddFriend. The two suppression flags are only set when the add succeeded and the earlier gate checks passed
/// (the gate short-circuits in order), so the host can log why a notice was withheld.
/// </summary>
public readonly record struct AddFriendResult(
    ushort ResultCode, bool Added, bool ShouldNotify, bool TargetListsRequester = false, bool TargetBlocksRequester = false);

/// <summary>
/// The add-friend decision from the write onward: the size-guarded add, its mapping to the 23508 result code, and the
/// 23512 notify-gate. The host keeps everything before the write (throttles, parse, list-full pre-check, name lookup,
/// self-add drop, per-pair limit) and everything after it (the answer record, the 23512 fan-out).
/// </summary>
public sealed class AddFriendUseCase
{
    private readonly IFriendListRepository _friends;
    private readonly IBlockListRepository _blocks;

    /// <summary>Creates the use case over the friend and block list ports.</summary>
    public AddFriendUseCase(IFriendListRepository friends, IBlockListRepository blocks)
    {
        _friends = friends;
        _blocks = blocks;
    }

    /// <param name="ownerId">The requester's character id.</param>
    /// <param name="targetId">The already-resolved target character id.</param>
    /// <param name="requesterHasUsername">Whether the requester's name is resolved: 23512 carries it, so no name means no notice.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AddFriendResult> ExecuteAsync(uint ownerId, uint targetId, bool requesterHasUsername, CancellationToken ct = default)
    {
        switch (await _friends.AddAsync(ownerId, targetId))
        {
            case AddOutcome.AlreadyFriend:
                return new AddFriendResult(SocialRules.AddResultAlreadyFriend, false, false);
            case AddOutcome.ListFull:
                return new AddFriendResult(SocialRules.AddResultListFull, false, false);
        }

        if (!requesterHasUsername)
            return new AddFriendResult(SocialRules.ResultOk, true, false);

        // No notice on an add-back: when the added player already lists the requester, no 23512 is sent.
        if (await _friends.ListsFriendAsync(targetId, ownerId, ct))
            return new AddFriendResult(SocialRules.ResultOk, true, false, TargetListsRequester: true);

        // Defensive: if the added player has blocked the requester, do not tell them they were added.
        if (await _blocks.BlocksAsync(targetId, ownerId, ct))
            return new AddFriendResult(SocialRules.ResultOk, true, false, TargetBlocksRequester: true);

        return new AddFriendResult(SocialRules.ResultOk, true, true);
    }
}
