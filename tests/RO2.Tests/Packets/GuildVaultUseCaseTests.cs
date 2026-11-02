using RO2.Guilds.Application;
using RO2.Guilds.Domain;
using RO2.Inventory.Domain;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>Pure tests of the guild vault item move: bag-before-vault order and the restore on a refused or failed store.</summary>
public class GuildVaultUseCaseTests
{
    private sealed class FakeInv(List<string> log) : IInventoryRepository
    {
        public InventoryItem? Row { get; set; }
        public bool AddFails { get; set; }
        public Task<IReadOnlyList<InventoryItem>> GetByCharacterAsync(uint c) => throw new NotSupportedException();
        public Task<IReadOnlyList<InventoryItem>> GetEquipmentAsync(uint c) => throw new NotSupportedException();
        public Task<InventoryItem?> GetByIdAsync(ulong id) { log.Add("get"); return Task.FromResult(Row); }
        public Task AddItemAsync(InventoryItem item)
        { log.Add("bag-add"); if (AddFails) throw new IOException("db"); Row = item; return Task.CompletedTask; }
        public Task RemoveItemAsync(ulong id) => throw new NotSupportedException();
        public Task<bool> RemoveItemAsync(ulong id, uint characterId) { log.Add("bag-remove"); Row = null; return Task.FromResult(true); }
        public Task UpdateItemAsync(InventoryItem item) => throw new NotSupportedException();
        public Task<ulong> GetNextIdAsync() => throw new NotSupportedException();
        public Task<int> RepairSlotsAsync(uint c) => throw new NotSupportedException();
    }

    private sealed class FakeVault(List<string> log) : IGuildVaultLedger
    {
        public VaultStoreOutcome Room { get; set; } = VaultStoreOutcome.Stored;
        public VaultStoreOutcome Store { get; set; } = VaultStoreOutcome.Stored;
        public bool StoreThrows { get; set; }
        public GuildStorageItem? Held { get; set; }
        public VaultStoreOutcome CheckRoom(Guild g, int capacity) { log.Add("room"); return Room; }
        public Task<VaultStoreOutcome> StoreAsync(Guild g, GuildStorageItem rec, int capacity)
        { log.Add("store"); if (StoreThrows) throw new InvalidOperationException(); return Task.FromResult(Store); }
        public void Remove(Guild g, ulong uid) => log.Add("vault-remove");
        public GuildStorageItem? Take(Guild g, byte slot) { log.Add("take"); return Held; }
        public void PutBack(Guild g, GuildStorageItem rec) => log.Add("putback");
        public Task PersistAsync(Guild g) { log.Add("persist"); return Task.CompletedTask; }
    }

    private static (GuildVaultUseCase Uc, FakeVault Vault, FakeInv Inv, List<string> Log) Make()
    {
        var log = new List<string>();
        var inv = new FakeInv(log) { Row = new InventoryItem { Id = 7, CharacterId = 5, ItemId = 100, Count = 3, StorageType = 0, Slot = 4 } };
        var vault = new FakeVault(log);
        return (new GuildVaultUseCase(vault), vault, inv, log);
    }

    private static InventoryItem Req() => new() { Id = 7, CharacterId = 5 };

    [Fact]
    public async Task Deposit_RemovesFromBagBeforeStoring()
    {
        var (uc, _, inv, log) = Make();
        var (stored, refusal) = await uc.DepositAsync(new Guild(), Req(), inv, 10);
        Assert.Equal(VaultDepositRefusal.None, refusal);
        Assert.Equal(7UL, stored!.Uid);
        Assert.Equal(100u, stored.ItemId);
        Assert.Equal(5u, stored.DepositedByCharId);
        Assert.Equal(new[] { "get", "room", "bag-remove", "store" }, log);
    }

    [Fact]
    public async Task Deposit_ItemGoneOrNotBag_TouchesNothingElse()
    {
        var (uc, _, inv, log) = Make();
        inv.Row!.StorageType = 1;
        var (stored, refusal) = await uc.DepositAsync(new Guild(), Req(), inv, 10);
        Assert.Null(stored);
        Assert.Equal(VaultDepositRefusal.NotMoved, refusal);
        Assert.Equal(new[] { "get" }, log);
    }

    [Fact]
    public async Task Deposit_NoRoom_RefusesBeforeLeavingTheBag()
    {
        var (uc, vault, inv, log) = Make();
        vault.Room = VaultStoreOutcome.Full;
        var (_, refusal) = await uc.DepositAsync(new Guild(), Req(), inv, 10);
        Assert.Equal(VaultDepositRefusal.VaultFull, refusal);
        Assert.Equal(new[] { "get", "room" }, log);
    }

    [Fact]
    public async Task Deposit_DisbandedAtRoomCheck_NotMoved()
    {
        var (uc, vault, inv, log) = Make();
        vault.Room = VaultStoreOutcome.NotRegistered;
        var (_, refusal) = await uc.DepositAsync(new Guild(), Req(), inv, 10);
        Assert.Equal(VaultDepositRefusal.NotMoved, refusal);
        Assert.Equal(new[] { "get", "room" }, log);
    }

    [Fact]
    public async Task Deposit_FullAtStore_RestoresTheBagThenDropsTheVaultCopy()
    {
        var (uc, vault, inv, log) = Make();
        vault.Store = VaultStoreOutcome.Full;
        var (stored, refusal) = await uc.DepositAsync(new Guild(), Req(), inv, 10);
        Assert.Null(stored);
        Assert.Equal(VaultDepositRefusal.VaultFull, refusal);
        Assert.Equal(new[] { "get", "room", "bag-remove", "store", "bag-add", "vault-remove" }, log);
        Assert.Equal(7UL, inv.Row!.Id);
    }

    [Fact]
    public async Task Deposit_DisbandedOrThrowingStore_RestoresAndIsNotMoved()
    {
        var (uc, vault, inv, log) = Make();
        vault.Store = VaultStoreOutcome.NotRegistered;
        Assert.Equal(VaultDepositRefusal.NotMoved, (await uc.DepositAsync(new Guild(), Req(), inv, 10)).Refusal);

        var (uc2, vault2, inv2, log2) = Make();
        vault2.StoreThrows = true;
        Assert.Equal(VaultDepositRefusal.NotMoved, (await uc2.DepositAsync(new Guild(), Req(), inv2, 10)).Refusal);
        Assert.Equal(new[] { "get", "room", "bag-remove", "store", "bag-add", "vault-remove" }, log2);
    }

    [Fact]
    public async Task Deposit_RestoreFailure_StillDropsTheVaultCopy()
    {
        var (uc, vault, inv, log) = Make();
        vault.Store = VaultStoreOutcome.Full;
        inv.AddFails = true;
        var (_, refusal) = await uc.DepositAsync(new Guild(), Req(), inv, 10);
        Assert.Equal(VaultDepositRefusal.VaultFull, refusal);
        Assert.Equal("vault-remove", log[^1]);
    }

    [Fact]
    public async Task Withdraw_EmptyCell_ReturnsNullWithoutWriting()
    {
        var (uc, _, inv, log) = Make();
        Assert.Null(await uc.WithdrawAsync(new Guild(), 3, 5, inv));
        Assert.Equal(new[] { "take" }, log);
    }

    [Fact]
    public async Task Withdraw_PersistsBeforeAddingToBagAndKeepsUid()
    {
        var (uc, vault, inv, log) = Make();
        vault.Held = new GuildStorageItem { Uid = 9, ItemId = 200, Count = 0, StorageSlot = 3 };
        var rec = await uc.WithdrawAsync(new Guild(), 3, 5, inv);
        Assert.Same(vault.Held, rec);
        Assert.Equal(new[] { "take", "persist", "bag-add" }, log);
        Assert.Equal(9UL, inv.Row!.Id);
        Assert.Equal(5u, inv.Row.CharacterId);
        Assert.Equal(1, inv.Row.Count);
        Assert.Equal((short)-1, inv.Row.Slot);
    }

    [Fact]
    public async Task Withdraw_BagAddFails_PutsTheRecordBackAndPersists()
    {
        var (uc, vault, inv, log) = Make();
        vault.Held = new GuildStorageItem { Uid = 9, ItemId = 200, Count = 2, StorageSlot = 3 };
        inv.AddFails = true;
        Assert.Null(await uc.WithdrawAsync(new Guild(), 3, 5, inv));
        Assert.Equal(new[] { "take", "persist", "bag-add", "putback", "persist" }, log);
    }
}
