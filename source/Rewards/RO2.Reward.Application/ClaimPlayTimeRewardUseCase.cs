using RO2.GameData.Tables;
using RO2.Rewards.Domain;

namespace RO2.Rewards.Application;

/// <summary>
/// Result of <see cref="ClaimPlayTimeRewardUseCase"/>.
/// </summary>
/// <param name="Claim">The chain's answer to send (2945), or null when the chain has no play-time row.</param>
/// <param name="GiftsInBox">Gifts in the box after the reward was filed; 0 when nothing was filed, so no refresh is due.</param>
/// <param name="Filed">The gifts filed for this claim; empty when none.</param>
/// <param name="FilingError">The storage error that stopped the reward from being filed, or null.</param>
public sealed record ClaimPlayTimeRewardResult(PlayTimeRewardChain.ClaimResult? Claim, int GiftsInBox,
    IReadOnlyList<AccountGift> Filed, Exception? FilingError);

/// <summary>
/// Claims the current play-time reward slot of an account's progress and files the granted row's reward items
/// into the account gift box.
/// </summary>
/// <remarks>
/// The progress, the chain and the clock come in as arguments; the use case holds no cache. Saving the progress
/// stays with its owner (the Zone host's play-time store orders its writes), so the caller saves when
/// <see cref="PlayTimeRewardChain.ClaimResult.GrantedRow"/> is set. A failure to file the reward is returned, not thrown.
/// </remarks>
public sealed class ClaimPlayTimeRewardUseCase
{
 /// <summary>Source code of play-time rewards in gift records.</summary>
    public const byte SourcePlayTime = 1;

    private readonly IAccountGiftRepository _gifts;

    /// <summary>Creates the use case over the gift repository.</summary>
    public ClaimPlayTimeRewardUseCase(IAccountGiftRepository gifts) => _gifts = gifts;

 /// <param name="progress">The account's progress.</param>
 /// <param name="chain">The play-time chain.</param>
 /// <param name="requestedSlot">The slot the client asked to claim.</param>
 /// <param name="loginTime">When the current stint began (UTC).</param>
 /// <param name="nowUtc">The current time (UTC); also the filing time of the gifts.</param>
 /// <param name="accountId">Account the reward is filed for; 0 files nothing.</param>
 /// <param name="newGiftGuid">Produces the id of each filed gift (32 hex digits).</param>
 /// <param name="nowLocal">The server wall clock the box uses for expiry.</param>
 /// <param name="ct">Cancellation token.</param>
    public async Task<ClaimPlayTimeRewardResult> ExecuteAsync(PlayTimeRewardProgress progress, Attendance[] chain,
        uint requestedSlot, DateTime loginTime, DateTime nowUtc, uint accountId, Func<string> newGiftGuid,
        DateTime nowLocal, CancellationToken ct = default)
    {
        PlayTimeRewardChain.ClaimResult? result;
        lock (progress.Gate)
            result = PlayTimeRewardChain.Claim(progress, chain, requestedSlot, loginTime, nowUtc, _ => true);

        var none = Array.Empty<AccountGift>();
        if (result is not { GrantedRow: { } row } || accountId == 0)
            return new ClaimPlayTimeRewardResult(result, 0, none, null);

        var filed = new List<AccountGift>(3);
        foreach (var (itemId, count) in new[] { (row.Reward_item1, row.Item_count1), (row.Reward_item2, row.Item_count2), (row.Reward_item3, row.Item_count3) })
        {
            if (itemId == 0 || count <= 0) continue;
            filed.Add(new AccountGift { Guid = newGiftGuid(), ItemId = itemId, Count = (ushort)Math.Min(count, ushort.MaxValue), ReceivedUtc = nowUtc, Source = SourcePlayTime });
        }
        if (filed.Count == 0)
            return new ClaimPlayTimeRewardResult(result, 0, none, null);

        try
        {
            int total = await _gifts.FileAsync(accountId, filed, nowLocal, ct);
            return new ClaimPlayTimeRewardResult(result, total, filed, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ClaimPlayTimeRewardResult(result, 0, none, ex);
        }
    }
}
