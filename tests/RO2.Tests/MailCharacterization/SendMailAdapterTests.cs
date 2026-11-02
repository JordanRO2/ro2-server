using RO2.BuildingBlocks;
using RO2.Mails.Application;
using RO2.Server.Zone.Services;
using RO2.Tests.Economy;
using Xunit;

namespace RO2.Tests.MailCharacterization;

/// <summary>The system-mail seam: the stored mail carries the draft's fields and no 2449 goes out unless asked.</summary>
public class SendMailAdapterTests
{
    private static readonly DateTime Sent = new(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc);

    private static MailDraft RewardDraft(bool notify = false) => new()
    {
        ReceiverCharId = 7, ReceiverName = "Rea", SenderName = "11809", Title = "11812", Body = "11813", Kind = 22, SentAt = Sent,
        Item = new ItemStackSnapshot { Id = 99, ItemId = 5001, Count = 3, Durability = 40 },
        Notify = notify,
    };

    [Fact]
    public async Task RewardDraft_StoresTheSameMailThePerSenderCodeBuilt_AndNotifiesNobody()
    {
        var repo = new FakeMailRepository(new RepoScheduler());
        var notified = new List<uint>();
        var adapter = new SendMailAdapter(repo, id => { notified.Add(id); return Task.CompletedTask; });

        await adapter.SendSystemMailAsync(RewardDraft());

        var m = Assert.Single(repo.Mails);
        Assert.Equal((7u, "Rea", "11809", "11812", "11813", (byte)22, Sent), (m.ReceiverCharId, m.ReceiverName, m.SenderName, m.Title, m.Body, m.Kind, m.SentAt));
        Assert.Equal((99UL, 5001u, 3, (byte)0, (short)40), (m.AttachedItemUid, m.AttachedItemId, m.AttachedItemCount, m.AttachedItemRefine, m.AttachedItemDurability));
        Assert.Equal((0L, 0L, 0u, false), (m.Gold, m.AuctionPrice, m.KindItemId, m.IsRead));
        Assert.Empty(notified);
    }

    [Fact]
    public async Task NotifyDraft_NotifiesTheReceiverAfterStoring()
    {
        var repo = new FakeMailRepository(new RepoScheduler());
        var notified = new List<uint>();
        var adapter = new SendMailAdapter(repo, id => { notified.Add(id); return Task.CompletedTask; });

        await adapter.SendSystemMailAsync(RewardDraft(notify: true));

        Assert.Single(repo.Mails);
        Assert.Equal([7u], notified);
    }
}
