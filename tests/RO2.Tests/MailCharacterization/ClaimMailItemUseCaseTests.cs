using RO2.Mails.Application;
using RO2.Mails.Domain;
using Xunit;

namespace RO2.Tests.MailCharacterization;

/// <summary>The pure 1396 claim decisions: 16203 for a missing/spent mail, 16201 without an inventory, 16207 with nowhere to put the item.</summary>
public class ClaimMailItemUseCaseTests
{
    [Fact]
    public void Claimable_checks_mail_then_inventory()
    {
        var live = new MailMessage { AttachedItemId = 5 };
        Assert.Equal(MailClaimVerdict.InvalidMail, ClaimMailItemUseCase.DecideClaimable(null, true));
        Assert.Equal(MailClaimVerdict.InvalidMail, ClaimMailItemUseCase.DecideClaimable(new MailMessage(), true));
        Assert.Equal(MailClaimVerdict.InvalidMail, ClaimMailItemUseCase.DecideClaimable(new MailMessage { AttachedItemId = 5, ItemClaimed = true }, true));
        Assert.Equal(MailClaimVerdict.InvalidMail, ClaimMailItemUseCase.DecideClaimable(null, false));   // the mail check comes first
        Assert.Equal(MailClaimVerdict.SystemError, ClaimMailItemUseCase.DecideClaimable(live, false));
        Assert.Equal(MailClaimVerdict.Accepted, ClaimMailItemUseCase.DecideClaimable(live, true));
    }

    [Theory]
    [InlineData(true, true, MailClaimVerdict.Accepted)]
    [InlineData(true, false, MailClaimVerdict.Accepted)]   // a joinable stack takes the item in a full bag
    [InlineData(false, true, MailClaimVerdict.Accepted)]
    [InlineData(false, false, MailClaimVerdict.NoSpace)]
    public void Placement_needs_a_stack_or_a_free_cell(bool hasJoinStack, bool hasFreeCell, MailClaimVerdict expected)
        => Assert.Equal(expected, ClaimMailItemUseCase.DecidePlacement(hasJoinStack, hasFreeCell));
}
