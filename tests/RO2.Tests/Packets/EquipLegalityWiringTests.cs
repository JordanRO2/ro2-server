using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;
using RO2.Inventory.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// SP0-EQUIP PASS 1h: the 1324 ReqPCItemSwap and 1346 ReqBankSwapItem handlers refuse a swap/move that would PLACE
/// an item into a worn slot the item may not legally occupy (wrong slot, under-level, wrong job, wrong sex), while a
/// legal equip, a ring into either ring cell, a swap with the worn partner, and a plain bag move all proceed. The
/// legal paths keep the exact 2327 answer the 1c index-safe swap produced; refusals use the handler's existing
/// revert path and leave the item where it was. Equip_Slot_Overlap is NOT enforced (unverified; see EquipRules).
/// </summary>
public sealed class EquipLegalityWiringTests
{
    private const uint CharId = 775533u;
    private const ulong SrcUid = 0x0E043DA80E61A001UL;
    private const ulong DstUid = 0x0E043DA80E61A002UL;

    // --- templates (RO2.GameData.ItemData) ---------------------------------------------------------------
    // A level-50 main-hand sword usable by job 7 (Require_Job bit 6 = 64), unisex; worn slot 6 only.
    private static readonly ItemData Sword = new() { ID = 1001, Item_Type = 1, Equip_Slot = 6, Equip_Type = 2, Require_Level = 50, Require_Job = 64, Require_Sex = 3 };
    // Chest armour, worn slot 2 (Equip_Type = 18 + slot), no requirements.
    private static readonly ItemData Armour = new() { ID = 1002, Item_Type = 2, Equip_Slot = 2, Equip_Type = 20 };
    // Ring, worn slot 10 OR 11 (rings may go in either cell, earrings may not).
    private static readonly ItemData Ring = new() { ID = 1003, Item_Type = 3, Equip_Slot = 10, Equip_Type = 26 };
    // Female-only chest armour.
    private static readonly ItemData FemaleArmour = new() { ID = 1004, Item_Type = 2, Equip_Slot = 2, Equip_Type = 20, Require_Sex = 2 };

    private static readonly ItemData[] AllTemplates = { Sword, Armour, Ring, FemaleArmour };

    /// <summary>A GameDataStore seeded with the test templates (Items has a private setter; set it via reflection,
    /// the one place a test needs to inject master data without a JSON round-trip).</summary>
    private static GameDataStore SeededStore()
    {
        var store = new GameDataStore();
        var dict = AllTemplates.ToDictionary(t => t.ID).ToFrozenDictionary();
        typeof(GameDataStore).GetProperty(nameof(GameDataStore.Items))!.SetValue(store, dict);
        return store;
    }

    /// <summary>A 1324 payload: compact envelope [u8 width=1][u8 len][blob]; blob holds the two 11-byte descriptors
    /// (SRC @8, DST @19), padded to 30 bytes.</summary>
    private static byte[] BuildSwap1324(byte srcSlot, byte srcBag, ulong srcUid, byte srcQty,
                                        byte dstSlot, byte dstBag, ulong dstUid)
    {
        var blob = new byte[30];
        blob[2] = 0xA8; blob[3] = 0xE2;       // msgTag 0xE2A8 (INV-H12)
        blob[4] = 0x11;                       // actor handle == the tests' PlayerState.EntityId (0x11)
        blob[8] = srcSlot; blob[9] = srcBag;
        BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(10), srcUid);
        blob[18] = srcQty;
        blob[19] = dstSlot; blob[20] = dstBag;
        BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(21), dstUid);
        var payload = new byte[2 + blob.Length];
        payload[0] = 1;
        payload[1] = (byte)blob.Length;
        blob.CopyTo(payload.AsSpan(2));
        return payload;
    }

    /// <summary>The non-equip/non-bag 2327 body: VarBlob32 { u16 result=0, PRIMARY = src at its new cell,
    /// VecU16(1) { DEST at its new cell } } — the oracle the 1c swap defined.</summary>
    private static byte[] ExpectedSwap2327(InventoryItem primaryAtNewCell, InventoryItem vectorAtNewCell)
    {
        var p = WorldEntryService.BuildItemRecord126(primaryAtNewCell);
        var v = WorldEntryService.BuildItemRecord126(vectorAtNewCell);
        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes((uint)(2 + p.Length + 2 + v.Length)));
        body.AddRange(BitConverter.GetBytes((ushort)0));
        body.AddRange(p);
        body.AddRange(BitConverter.GetBytes((ushort)1));
        body.AddRange(v);
        return body.ToArray();
    }

    private static (PlayerStateManager players, BroadcastService broadcast, PlayerState player) Wire(
        Harness net, byte level = 60, byte job = 7, byte gender = 1)
    {
        var players = new PlayerStateManager(new NoDirtySink());
        // Character left null so the equip-refresh (EffectService/StatService) block is skipped; the wiring under
        // test runs before it. Level/Job/Gender are the EquipActor facts CanPlace checks.
        var player = new PlayerState
        {
            SessionId = net.Sessions[0].SessionId, CharacterId = CharId, Name = "Hero", EntityId = 0x11u,
            Level = level, Job = job, Gender = gender,
        };
        players.AddPlayer(player.SessionId, player);
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(net.Server);
        return (players, broadcast, player);
    }

    private static ItemSwapHandler BuildSwapHandler(IInventoryRepository inv, GameDataStore store,
        PlayerStateManager players, BroadcastService broadcast)
    {
        var services = new ServiceCollection()
            .AddScoped(_ => inv)
            .AddSingleton(store)
            .AddSingleton(TestEmblems.Create())
            .BuildServiceProvider();
        return new ItemSwapHandler(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ItemSwapHandler>.Instance, players, broadcast);
    }

    private static InventoryItem Bag(ulong id, uint itemId, short slot) =>
        new() { Id = id, CharacterId = CharId, ItemId = itemId, Count = 1, StorageType = 0, Slot = slot };

    private static InventoryItem Worn(ulong id, uint itemId, short slot) =>
        new() { Id = id, CharacterId = CharId, ItemId = itemId, Count = 1, StorageType = 1, Slot = slot };

    // --- refusals: an illegal placement into a worn slot reverts and does not move the item ----------------------

    [Theory]
    [InlineData(1001u, (byte)7)]   // a sword into the off-hand cell (WrongSlot: sword is worn slot 6 only)
    public async Task EquipIntoTheWrongSlot_IsRefused_AndTheItemStays(uint itemId, byte wornSlot)
    {
        await using var net = await Harness.StartAsync(1);
        var (players, broadcast, _) = Wire(net);
        var src = Bag(SrcUid, itemId, 0);
        var bag = new GearInventory([src]);
        var handler = BuildSwapHandler(bag, SeededStore(), players, broadcast);

        await handler.HandleAsync(net.Sessions[0], BuildSwap1324(50, 1, SrcUid, 1, wornSlot, 1, 0));

        var (op, _) = await net.Readers[0].NextAsync();
        Assert.Equal(ZoneS2COpcodes.OnSwapItem, op);
        var after = bag.Items.Single(i => i.Id == SrcUid);
        Assert.Equal((byte)0, after.StorageType);   // not equipped
        Assert.Equal((short)0, after.Slot);
    }

    [Fact]
    public async Task EquipUnderLevel_IsRefused()
    {
        await using var net = await Harness.StartAsync(1);
        var (players, broadcast, _) = Wire(net, level: 49);   // sword needs 50
        var src = Bag(SrcUid, Sword.ID, 0);
        var bag = new GearInventory([src]);
        var handler = BuildSwapHandler(bag, SeededStore(), players, broadcast);

        await handler.HandleAsync(net.Sessions[0], BuildSwap1324(50, 1, SrcUid, 1, 6, 1, 0));

        await net.Readers[0].NextAsync();
        Assert.Equal((byte)0, bag.Items.Single(i => i.Id == SrcUid).StorageType);
    }

    [Fact]
    public async Task EquipWrongJob_IsRefused()
    {
        await using var net = await Harness.StartAsync(1);
        var (players, broadcast, _) = Wire(net, job: 6);   // sword needs bit 6 (job 7); job 6 is bit 5
        var src = Bag(SrcUid, Sword.ID, 0);
        var bag = new GearInventory([src]);
        var handler = BuildSwapHandler(bag, SeededStore(), players, broadcast);

        await handler.HandleAsync(net.Sessions[0], BuildSwap1324(50, 1, SrcUid, 1, 6, 1, 0));

        await net.Readers[0].NextAsync();
        Assert.Equal((byte)0, bag.Items.Single(i => i.Id == SrcUid).StorageType);
    }

    [Fact]
    public async Task EquipWrongSex_IsRefused()
    {
        await using var net = await Harness.StartAsync(1);
        var (players, broadcast, _) = Wire(net, gender: 1);   // female-only armour, male player
        var src = Bag(SrcUid, FemaleArmour.ID, 0);
        var bag = new GearInventory([src]);
        var handler = BuildSwapHandler(bag, SeededStore(), players, broadcast);

        await handler.HandleAsync(net.Sessions[0], BuildSwap1324(50, 1, SrcUid, 1, 2, 1, 0));

        await net.Readers[0].NextAsync();
        Assert.Equal((byte)0, bag.Items.Single(i => i.Id == SrcUid).StorageType);
    }

    // --- legal placements proceed -------------------------------------------------------------------------------

    [Fact]
    public async Task LegitEquipIntoAnEmptySlot_Succeeds_AndPerformsTheMove()
    {
        await using var net = await Harness.StartAsync(1);
        var (players, broadcast, _) = Wire(net);
        var src = Bag(SrcUid, Sword.ID, 0);
        var bag = new GearInventory([src]);
        var handler = BuildSwapHandler(bag, SeededStore(), players, broadcast);

        await handler.HandleAsync(net.Sessions[0], BuildSwap1324(50, 1, SrcUid, 1, 6, 1, 0));

        var (op, _) = await net.Readers[0].NextAsync();
        Assert.Equal(ZoneS2COpcodes.OnSwapItem, op);
        var after = bag.Items.Single(i => i.Id == SrcUid);
        Assert.Equal((byte)1, after.StorageType);   // now worn
        Assert.Equal((short)6, after.Slot);
    }

    [Theory]
    [InlineData((byte)10)]
    [InlineData((byte)11)]
    public async Task ARingEquipsIntoEitherRingCell(byte ringCell)
    {
        await using var net = await Harness.StartAsync(1);
        var (players, broadcast, _) = Wire(net);
        var src = Bag(SrcUid, Ring.ID, 0);
        var bag = new GearInventory([src]);
        var handler = BuildSwapHandler(bag, SeededStore(), players, broadcast);

        await handler.HandleAsync(net.Sessions[0], BuildSwap1324(50, 1, SrcUid, 1, ringCell, 1, 0));

        var (op, _) = await net.Readers[0].NextAsync();
        Assert.Equal(ZoneS2COpcodes.OnSwapItem, op);
        var after = bag.Items.Single(i => i.Id == SrcUid);
        Assert.Equal((byte)1, after.StorageType);
        Assert.Equal((short)ringCell, after.Slot);
    }

    [Fact]
    public async Task SwapWithTheWornPartner_Proceeds_IndexSafe_AndKeepsTheByteExact2327()
    {
        await using var net = await Harness.StartAsync(1);
        var (players, broadcast, _) = Wire(net);
        // A sword in the bag (flat 0) swapped with a sword worn at slot 6: the destination cell is occupied by the
        // swap partner, which must be allowed (vacatingUid), and the move must still happen.
        var src = Bag(SrcUid, Sword.ID, 0);
        var dst = Worn(DstUid, Sword.ID, 6);
        var bag = new GearInventory([src, dst]);
        var handler = BuildSwapHandler(bag, SeededStore(), players, broadcast);

        await handler.HandleAsync(net.Sessions[0], BuildSwap1324(50, 1, SrcUid, 1, 6, 1, DstUid));

        var (op, body) = await net.Readers[0].NextAsync();
        Assert.Equal(ZoneS2COpcodes.OnSwapItem, op);
        Assert.Equal((byte)1, bag.Items.Single(i => i.Id == SrcUid).StorageType);   // src now worn at 6
        Assert.Equal((short)6, bag.Items.Single(i => i.Id == SrcUid).Slot);
        Assert.Equal((byte)0, bag.Items.Single(i => i.Id == DstUid).StorageType);   // dst settled into the bag cell
        Assert.Equal((short)0, bag.Items.Single(i => i.Id == DstUid).Slot);
        Assert.Equal(2, bag.Items.Count);
        // Same framing as the 1c swap: PRIMARY = src at new cell, VECTOR = dst at new cell.
        Assert.Equal(ExpectedSwap2327(src, dst), body);
    }

    [Fact]
    public async Task APlainBagToBagMove_IsUnaffected()
    {
        await using var net = await Harness.StartAsync(1);
        var (players, broadcast, _) = Wire(net);
        // Two bag rows with real templates: no worn target, so the equip gate never fires and the 2327 is the
        // exact 1c oracle.
        var src = Bag(SrcUid, Sword.ID, 0);
        var dst = Bag(DstUid, Armour.ID, 1);
        var bag = new GearInventory([src, dst]);
        var handler = BuildSwapHandler(bag, SeededStore(), players, broadcast);

        await handler.HandleAsync(net.Sessions[0], BuildSwap1324(50, 1, SrcUid, 1, 51, 1, DstUid));

        var (op, body) = await net.Readers[0].NextAsync();
        Assert.Equal(ZoneS2COpcodes.OnSwapItem, op);
        Assert.Equal((short)1, bag.Items.Single(i => i.Id == SrcUid).Slot);
        Assert.Equal((short)0, bag.Items.Single(i => i.Id == DstUid).Slot);
        Assert.All(bag.Items, i => Assert.Equal((byte)0, i.StorageType));
        Assert.Equal(ExpectedSwap2327(src, dst), body);
    }

    // --- bank swap: the same gate on the 1346 path --------------------------------------------------------------

    private static byte[] BuildBankSwap1346(byte srcContainer, ulong srcUid, byte destContainer, byte destSlot, ulong destUid)
    {
        var blob = new byte[40];
        blob[16] = srcContainer;
        BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(19), srcUid);
        blob[28] = destContainer;
        blob[29] = destSlot;
        BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(31), destUid);
        var payload = new byte[2 + blob.Length];
        payload[0] = 1;
        payload[1] = (byte)blob.Length;
        blob.CopyTo(payload.AsSpan(2));
        return payload;
    }

    private static BankSwapItemHandler BuildBankHandler(IInventoryRepository inv, GameDataStore store,
        PlayerStateManager players, BroadcastService broadcast)
    {
        var services = new ServiceCollection()
            .AddScoped(_ => inv)
            .AddSingleton(store)
            .BuildServiceProvider();
        return new BankSwapItemHandler(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<BankSwapItemHandler>.Instance, players, broadcast);
    }

    [Fact]
    public async Task BankSwap_MovingIntoAWrongWornSlot_IsRefused_AndTheItemStays()
    {
        await using var net = await Harness.StartAsync(1);
        var (players, broadcast, _) = Wire(net);
        // A sword parked in the bank (storage 2, slot 0); a crafted 1346 asks to move it into worn slot 2 (armour
        // slot) via container 1. MapPos -> StorageType 1, which the equip gate must refuse (WrongSlot).
        var src = new InventoryItem { Id = SrcUid, CharacterId = CharId, ItemId = Sword.ID, Count = 1, StorageType = 2, Slot = 0 };
        var bag = new GearInventory([src]);
        var handler = BuildBankHandler(bag, SeededStore(), players, broadcast);

        await handler.HandleAsync(net.Sessions[0], BuildBankSwap1346(srcContainer: 5, SrcUid, destContainer: 1, destSlot: 2, destUid: 0));

        var (op, _) = await net.Readers[0].NextAsync();
        Assert.Equal(ZoneS2COpcodes.OnBankData, op);   // SendBankRefresh (the handler's revert)
        var after = bag.Items.Single(i => i.Id == SrcUid);
        Assert.Equal((byte)2, after.StorageType);   // still in the bank
        Assert.Equal((short)0, after.Slot);
    }
}
