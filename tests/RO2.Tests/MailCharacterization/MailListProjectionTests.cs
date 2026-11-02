using RO2.Mails.Domain;
using Xunit;

namespace RO2.Tests.MailCharacterization;

/// <summary>The pure 2444 row projection, pinned to the S1 list characterization (claimed hidden, template id for kinds 1..11 only).</summary>
public class MailListProjectionTests
{
    private static MailMessage Mail(Action<MailMessage> set)
    {
        var m = new MailMessage { Uid = 7, SenderName = "Snd", Title = "Ttl", KindItemId = 777 };
        set(m);
        return m;
    }

    [Fact]
    public void Unclaimed_zeny_and_item_show_and_claimed_ones_hide()
    {
        var shown = MailListProjection.Project(Mail(m => { m.Gold = 500; m.AttachedItemId = 501; m.AttachedItemUid = 9001; }));
        Assert.Equal((500L, 9001UL), (shown.Gold, shown.ItemUid));

        var hidden = MailListProjection.Project(Mail(m =>
        { m.Gold = 500; m.GoldClaimed = true; m.AttachedItemId = 501; m.AttachedItemUid = 9001; m.ItemClaimed = true; }));
        Assert.Equal((0L, 0UL), (hidden.Gold, hidden.ItemUid));
    }

    [Theory]
    [InlineData((byte)0, 0u)]
    [InlineData((byte)1, 777u)]
    [InlineData((byte)11, 777u)]
    [InlineData((byte)12, 0u)]
    [InlineData((byte)22, 0u)]
    public void Template_id_shows_only_for_kinds_1_to_11(byte kind, uint expected) =>
        Assert.Equal(expected, MailListProjection.Project(Mail(m => m.Kind = kind)).ItemTemplateId);

    [Fact]
    public void Sender_and_title_pass_through_as_stored()
    {
        var player = MailListProjection.Project(Mail(_ => { }));
        Assert.Equal(("Snd", "Ttl"), (player.SenderName, player.Title));
        var auction = MailListProjection.Project(Mail(m => { m.Kind = 2; m.SenderName = ""; m.Title = ""; }));
        Assert.Equal(("", ""), (auction.SenderName, auction.Title));
    }
}
