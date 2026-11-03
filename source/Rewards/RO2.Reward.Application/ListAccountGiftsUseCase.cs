using RO2.Rewards.Domain;

namespace RO2.Rewards.Application;

/// <summary>One page of an account's gift box: the live gifts (newest first) and the window the page lists.</summary>
public sealed record AccountGiftPage(IReadOnlyList<AccountGift> NewestFirst, int Start, int Count);

/// <summary>Reads the account's live gifts and the window of the requested page.</summary>
/// <remarks>An account id of 0 has an empty box. A failure to read the box is thrown.</remarks>
public sealed class ListAccountGiftsUseCase
{
    private readonly IAccountGiftRepository _gifts;

    /// <summary>Creates the use case over the gift repository.</summary>
    public ListAccountGiftsUseCase(IAccountGiftRepository gifts) => _gifts = gifts;

 /// <param name="accountId">Account whose box is listed; 0 lists an empty box.</param>
 /// <param name="page">The requested page.</param>
 /// <param name="nowLocal">The server wall clock the box uses for expiry.</param>
 /// <param name="ct">Cancellation token.</param>
    public async Task<AccountGiftPage> ExecuteAsync(uint accountId, ushort page, DateTime nowLocal, CancellationToken ct = default)
    {
        IReadOnlyList<AccountGift> gifts = accountId == 0
            ? Array.Empty<AccountGift>()
            : await _gifts.GetLiveAsync(accountId, nowLocal, ct);
        var (start, count) = AccountGiftRules.PageWindow(gifts.Count, page);
        return new AccountGiftPage(gifts, start, count);
    }
}
