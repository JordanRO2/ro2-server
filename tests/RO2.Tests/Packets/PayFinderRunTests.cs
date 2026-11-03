using Microsoft.Extensions.Logging.Abstractions;
using RO2.BuildingBlocks;
using RO2.DungeonFinder.Application;
using RO2.DungeonFinder.Domain;
using RO2.GameData;
using RO2.GameData.Tables;
using RO2.Inventory.Domain;
using RO2.Mails.Application;
using RO2.Mails.Domain;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>Pins the order of the finder pay flow: bag path write, landed, credit, 2816 InBag; mail path mail, credit, 2816 Mailed.</summary>
public sealed class PayFinderRunTests
{
    private static readonly FindDungeonList List = new() { List_ID = 21, Category_ID = 5, Reward_GroupID = 3, Last_Boss_ID = 1 };
    private static readonly FindDungeonCategory[] Categories = [];
    private static readonly FindDungeonReward[] Rewards =
        [new() { ID = 3, Reward_GroupID = 3, MinLevel = 1, MaxLevel = 255, Money = 1000, Exp = 2500, _1st_Item_ID = 100, _1st_Item_Num = 1, _2nd_Item_ID = 200, _2nd_Item_Num = 250 }];
    private static readonly Dictionary<uint, ItemData> Templates = new()
    {
        [100] = new ItemData { ID = 100, Stack_Max = 99 },
        [200] = new ItemData { ID = 200, Stack_Max = 99 },
    };

    private sealed class Log
    {
        public readonly List<string> Events = [];
    }

    private sealed class Inv(Log log, IReadOnlyList<InventoryItem> owned) : IInventoryRepository
    {
        public Task<IReadOnlyList<InventoryItem>> GetByCharacterAsync(uint characterId) => Task.FromResult(owned);
        public Task<IReadOnlyList<InventoryItem>> GetEquipmentAsync(uint characterId) => Task.FromResult<IReadOnlyList<InventoryItem>>([]);
        public Task<InventoryItem?> GetByIdAsync(ulong id) => Task.FromResult<InventoryItem?>(null);
        public Task AddItemAsync(InventoryItem item) { log.Events.Add($"add {item.ItemId}x{item.Count}"); return Task.CompletedTask; }
        public Task RemoveItemAsync(ulong itemId) => Task.CompletedTask;
        public Task UpdateItemAsync(InventoryItem item) { log.Events.Add($"update {item.ItemId}x{item.Count}"); return Task.CompletedTask; }
        public Task<ulong> GetNextIdAsync() => Task.FromResult(7UL);
        public Task<int> RepairSlotsAsync(uint characterId) => Task.FromResult(0);
    }

    private sealed class Mail(Log log, bool fail = false) : ISendMail
    {
        public Task<MailMessage> SendSystemMailAsync(MailDraft draft)
        {
            if (fail) throw new InvalidOperationException("store down");
            log.Events.Add($"mail {draft.Item!.ItemId}x{draft.Item.Count} kind{draft.Kind}");
            return Task.FromResult<MailMessage>(null!);
        }
    }

    private sealed class Wallet(Log log) : IWallet
    {
        public long Gold => 0;
        public long BankGold => 0;
        public bool TryDebit(long amount) => false;
        public long DebitUpTo(long amount) => 0;
        public void Credit(long amount) => log.Events.Add($"credit {amount}");
    }

    private sealed class Publisher(Log log) : IFinderRewardPublisher
    {
        public Task ItemLanded(uint characterId, uint itemId, int count, IReadOnlyList<InventoryItem> landedStacks)
        { log.Events.Add($"landed {itemId}x{count} stacks{landedStacks.Count}"); return Task.CompletedTask; }
        public Task Completed(uint characterId, uint listId, CompleteGiftState state)
        { log.Events.Add($"2816 {listId} {state}"); return Task.CompletedTask; }
    }

    private static Task<PayFinderRunResult> Pay(Log log, IReadOnlyList<InventoryItem> owned, bool mailFails = false)
        => new PayFinderRun(new Inv(log, owned), new Mail(log, mailFails), new Publisher(log), NullLogger<PayFinderRun>.Instance)
            .PayAsync(7, "pc", 50, 0, List, new Wallet(log), Categories, Rewards, Templates);

    [Fact]
    public async Task BagPath_WritesThenNotifiesPerLanding_ThenCredits_ThenSends2816InBag()
    {
        var log = new Log();
        var result = await Pay(log, []);
        Assert.Equal(PayOutcome.Paid, result.Outcome);
        Assert.Equal(["add 100x1", "landed 100x1 stacks1", "add 200x99", "add 200x99", "add 200x52", "landed 200x250 stacks3",
            "credit 1000", "2816 21 InBag"], log.Events);
    }

    [Fact]
    public async Task MailPath_MailsThenCredits_ThenSends2816Mailed_WithNoPickup()
    {
        var log = new Log();
        var full = Enumerable.Range(0, 20)
            .Select(i => new InventoryItem { Id = (ulong)i + 1, CharacterId = 7, ItemId = 60000000u + (uint)i, Count = 1, Slot = (short)i }).ToList();
        var result = await Pay(log, full);
        Assert.Equal(PayOutcome.Mailed, result.Outcome);
        Assert.Equal(4, result.Mails);
        Assert.Equal(["mail 100x1 kind22", "mail 200x99 kind22", "mail 200x99 kind22", "mail 200x52 kind22",
            "credit 1000", "2816 21 Mailed"], log.Events);
    }

    [Fact]
    public async Task MailPath_WhenNoMailCanBeStored_PaysNothing_AndSendsNo2816()
    {
        var log = new Log();
        var full = Enumerable.Range(0, 20)
            .Select(i => new InventoryItem { Id = (ulong)i + 1, CharacterId = 7, ItemId = 60000000u + (uint)i, Count = 1, Slot = (short)i }).ToList();
        var result = await Pay(log, full, mailFails: true);
        Assert.Equal(PayOutcome.NotPaid, result.Outcome);
        Assert.Empty(log.Events);
    }
}
