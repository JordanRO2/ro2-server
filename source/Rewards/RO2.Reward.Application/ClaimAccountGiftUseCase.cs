using RO2.Rewards.Domain;

namespace RO2.Rewards.Application;

/// <summary>Outcome of <see cref="ClaimAccountGiftUseCase"/>.</summary>
public enum ClaimAccountGiftStatus
{
 /// <summary>The box does not hold the gift (or it expired); nothing is answered.</summary>
    NotFound,

 /// <summary>The claimant's bag has no room; the gift stays in the box.</summary>
    BagFull,

 /// <summary>The gift was taken out of the box; the caller lands it in the bag.</summary>
    Claimed,

 /// <summary>Another claim took the gift first; nothing is answered.</summary>
    StoreFailed,
}

/// <summary>Result of <see cref="ClaimAccountGiftUseCase"/>: the status and the gift it concerns (null for NotFound).</summary>
public sealed record ClaimAccountGiftResult(ClaimAccountGiftStatus Status, AccountGift? Gift);

/// <summary>
/// Decides an account gift claim: finds the live gift, asks whether the bag has room, and takes the gift out of the box.
/// </summary>
/// <remarks>
/// Landing the gift in the bag, filing it back when nothing landed, and the follow-up notifications stay with the
/// caller. A failure to read the box is thrown, not returned.
/// </remarks>
public sealed class ClaimAccountGiftUseCase
{
    private readonly IAccountGiftRepository _gifts;

    /// <summary>Creates the use case over the gift repository.</summary>
    public ClaimAccountGiftUseCase(IAccountGiftRepository gifts) => _gifts = gifts;

 /// <param name="accountId">Account whose box is claimed from.</param>
 /// <param name="guid">The gift id (32 hex digits).</param>
 /// <param name="nowLocal">The server wall clock the box uses for expiry.</param>
 /// <param name="bagHasRoom">Whether the claimant's bag can take the gift.</param>
 /// <param name="ct">Cancellation token.</param>
    public async Task<ClaimAccountGiftResult> ExecuteAsync(uint accountId, string guid, DateTime nowLocal,
        Func<AccountGift, Task<bool>> bagHasRoom, CancellationToken ct = default)
    {
        var gift = (await _gifts.GetLiveAsync(accountId, nowLocal, ct)).FirstOrDefault(g => g.Guid == guid);
        if (gift is null)
            return new ClaimAccountGiftResult(ClaimAccountGiftStatus.NotFound, null);
        if (!await bagHasRoom(gift))
            return new ClaimAccountGiftResult(ClaimAccountGiftStatus.BagFull, gift);
        if (!await _gifts.TryTakeAsync(accountId, guid, ct))
            return new ClaimAccountGiftResult(ClaimAccountGiftStatus.StoreFailed, gift);
        return new ClaimAccountGiftResult(ClaimAccountGiftStatus.Claimed, gift);
    }
}
