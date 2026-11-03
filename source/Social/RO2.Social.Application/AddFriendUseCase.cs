using RO2.Socials.Domain;

namespace RO2.Socials.Application;

/// <summary>What an add-friend decided: the 23508 result code and whether the friend was added.</summary>
public readonly record struct AddFriendResult(ushort ResultCode, bool Added);

/// <summary>
/// Whether the added player gets a 23512 NfyAddFriend. The two suppression flags say which gate check withheld it (the
/// gate short-circuits in order), so the host can log why.
/// </summary>
public readonly record struct FriendNoticeDecision(bool ShouldNotify, bool TargetListsRequester = false, bool TargetBlocksRequester = false);

/// <summary>
/// The add-friend decisions: the size-guarded add mapped to the 23508 result code, and, as a separate call because the host
/// sends 23508 between them, the 23512 notify-gate. The host keeps everything before the write (throttles, parse, list-full
/// pre-check, name lookup, self-add drop, per-pair limit) and the wire (the answer record, the 23512 fan-out).
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
    public async Task<AddFriendResult> ExecuteAsync(uint ownerId, uint targetId)
    {
        return await _friends.AddAsync(ownerId, targetId) switch
        {
            AddOutcome.AlreadyFriend => new AddFriendResult(SocialRules.AddResultAlreadyFriend, false),
            AddOutcome.ListFull => new AddFriendResult(SocialRules.AddResultListFull, false),
            _ => new AddFriendResult(SocialRules.ResultOk, true),
        };
    }

    /// <summary>The 23512 gate for a successful add: runs after the host has sent the 23508 answer.</summary>
    /// <param name="ownerId">The requester's character id.</param>
    /// <param name="targetId">The already-resolved target character id.</param>
    /// <param name="requesterHasUsername">Whether the requester's name is resolved: 23512 carries it, so no name means no notice.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<FriendNoticeDecision> ShouldNotifyAsync(uint ownerId, uint targetId, bool requesterHasUsername, CancellationToken ct = default)
    {
        if (!requesterHasUsername)
            return new FriendNoticeDecision(false);

        // No notice on an add-back: when the added player already lists the requester, no 23512 is sent.
        if (await _friends.ListsFriendAsync(targetId, ownerId, ct))
            return new FriendNoticeDecision(false, TargetListsRequester: true);

        // Defensive: if the added player has blocked the requester, do not tell them they were added.
        if (await _blocks.BlocksAsync(targetId, ownerId, ct))
            return new FriendNoticeDecision(false, TargetBlocksRequester: true);

        return new FriendNoticeDecision(true);
    }
}
