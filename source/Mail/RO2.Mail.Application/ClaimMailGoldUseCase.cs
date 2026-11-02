using RO2.Mails.Domain;

namespace RO2.Mails.Application;

/// <summary>The mail a 1457 claim found (null when there is none for the reader) and the zeny it took (0 when nothing was claimable).</summary>
public readonly record struct MailGoldClaim(MailMessage? Mail, long Granted);

/// <summary>
/// The store side of a 1457 zeny claim: one atomic flip of <c>GoldClaimed</c> decides the winner, then an auction mail
/// whose zeny was just taken and which has nothing left to claim is removed. A missing, foreign or empty mail is not a
/// failure: it comes back with 0 granted and the host still answers 2617 with the wallet unchanged. The wallet credit,
/// its persistence and the 2617 (running total at blob+18) stay in the Zone host; the auction-kind test comes in as an
/// argument so this use case never reaches for the auction domain or game data.
/// </summary>
public sealed class ClaimMailGoldUseCase(IMailRepository mails)
{
    /// <summary>Claims the zeny of the reader's mail <paramref name="uid"/>; see the type remarks.</summary>
    /// <param name="charId">The reader (the mail's receiver).</param>
    /// <param name="uid">The mail uid.</param>
    /// <param name="isAuctionKind">Whether a mail kind is an auction kind (the host's <c>AuctionMailKind.IsAuction</c>).</param>
    /// <param name="onRemoveFailed">Told when removing the spent auction mail threw; the zeny is already claimed, so the claim still stands.</param>
    public async Task<MailGoldClaim> ClaimAsync(uint charId, ulong uid, Func<byte, bool> isAuctionKind, Action<MailMessage, Exception>? onRemoveFailed = null)
    {
        var claimed = await mails.TryClaimGoldAsync(charId, uid);
        if (claimed == null) return new MailGoldClaim(await mails.GetByUidAsync(charId, uid), 0);
        if (MailRules.ClaimRemovesMail(isAuctionKind(claimed.Kind), claimed))
        {
            try
            {
                await mails.DeleteIfSpentAsync(claimed.ReceiverCharId, claimed.Uid);
            }
            catch (Exception ex)
            {
                onRemoveFailed?.Invoke(claimed, ex);
            }
        }
        return new MailGoldClaim(claimed, claimed.Gold);
    }
}
