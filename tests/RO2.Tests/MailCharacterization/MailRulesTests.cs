using RO2.Mails.Domain;
using Xunit;

namespace RO2.Tests.MailCharacterization;

/// <summary>The pure MailRules decisions, pinned to the values the S1 characterization net already holds the host to.</summary>
public class MailRulesTests
{
    [Fact]
    public void Fee_limit_lifetime_and_dispositions_keep_their_values()
    {
        Assert.Equal(10L, MailRules.PostageZeny);
        Assert.Equal(31, MailRules.MailboxPlayerSendLimit);
        Assert.Equal(30, MailRules.MailLifetimeDays);
        Assert.Equal((byte)1, MailRules.ClaimDisposition(false));
        Assert.Equal((byte)2, MailRules.ClaimDisposition(true));
    }

    [Theory]
    [InlineData(0L, true, 10L)]
    [InlineData(500L, true, 510L)]
    [InlineData(long.MaxValue - 10, true, long.MaxValue)]
    [InlineData(long.MaxValue - 9, false, 0L)]
    public void Total_charge_is_zeny_plus_flat_postage_and_refuses_overflow(long gold, bool ok, long total)
    {
        Assert.Equal(ok, MailRules.TryTotalCharge(gold, out var t));
        Assert.Equal(total, t);
    }

    [Theory]
    [InlineData(-1L, 100L, false)]
    [InlineData(0L, 100L, true)]
    [InlineData(100L, 100L, true)]
    [InlineData(101L, 100L, false)]
    public void Zeny_attach_is_bounded_by_the_cap(long gold, long cap, bool ok) => Assert.Equal(ok, MailRules.IsAttachableZeny(gold, cap));

    [Theory]
    [InlineData("1000000", 1000000L)]
    [InlineData(null, long.MaxValue)]
    [InlineData("abc", long.MaxValue)]
    public void Zeny_cap_reads_config_def_92_or_is_unbounded(string? value, long cap) => Assert.Equal(cap, MailRules.ZenyCap(value));

    [Theory]
    [InlineData(30, true)]
    [InlineData(31, false)]
    public void A_mail_is_live_up_to_exactly_the_lifetime(int ageDays, bool live)
    {
        var now = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(live, MailRules.IsLive(now.AddDays(-ageDays), now));
        Assert.Equal(now.AddDays(-30), MailRules.LiveCutoff(now));
        Assert.Equal(now.AddDays(30), MailRules.ExpiresAt(now));
    }

    [Theory]
    [InlineData(30L, false)]
    [InlineData(31L, true)]
    public void Mailbox_is_full_at_the_limit(long held, bool full) => Assert.Equal(full, MailRules.IsMailboxFull(held));

    [Fact]
    public void Mark_read_flips_once()
    {
        var m = new MailMessage();
        Assert.True(m.MarkRead());
        Assert.True(m.IsRead);
        Assert.False(m.MarkRead());
    }

    [Theory]
    [InlineData(501u, false, 0L, false, true, false)]
    [InlineData(501u, true, 0L, false, false, false)]
    [InlineData(0u, false, 9L, false, false, true)]
    [InlineData(0u, false, 9L, true, false, false)]
    public void Claimable_item_and_gold(uint item, bool itemClaimed, long gold, bool goldClaimed, bool itemOk, bool goldOk)
    {
        var m = new MailMessage { AttachedItemId = item, ItemClaimed = itemClaimed, Gold = gold, GoldClaimed = goldClaimed };
        Assert.Equal(itemOk, MailRules.HasClaimableItem(m));
        Assert.Equal(goldOk, MailRules.HasClaimableGold(m));
    }

    [Fact]
    public void Claim_removes_only_a_spent_auction_mail()
    {
        var spent = new MailMessage { AttachedItemId = 501, ItemClaimed = true };
        var pending = new MailMessage { AttachedItemId = 501 };
        Assert.True(MailRules.ClaimRemovesMail(true, spent));
        Assert.False(MailRules.ClaimRemovesMail(true, pending));
        Assert.False(MailRules.ClaimRemovesMail(false, spent));
    }
}
