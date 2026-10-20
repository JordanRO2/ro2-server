using System.Buffers.Binary;
using System.Collections.Frozen;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Domain.GameData.Tables;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Tests.Economy;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.Packets;

/// <summary>
/// Cluster D hardening: MV-1 (1526 length + map-loaded gate), CB-H9-refine (1668 eligibility and catalyst location),
/// CB-H11 (1565 magnitude guard) and CB-H12 (1682 defensive Equip_Slot rule).
/// </summary>
public sealed class MovementAndItemOpsHardeningTests
{
    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }

    // ============================== MV-1: 1526 ==============================

    [Fact]
    public void Mv1_OnlyThe97ByteLengthIsCanonical()
    {
        foreach (var len in new[] { 0, 8, 60, 93, 96, 98, 101 })
            Assert.False(ViewGroupMoveHandler.IsCanonicalLength(len), $"length {len}");
        Assert.True(ViewGroupMoveHandler.IsCanonicalLength(97));
        // 97 is the length whose relay body is the 93-byte 2750.
        Assert.Equal(93, ViewGroupMoveHandler.BuildMoveBody(1, new byte[97]).Length);
    }

    [Fact]
    public void Mv1_MovesAreNotAcceptedBeforeMapLoadedOrDuringAGliderReentry()
    {
        Assert.False(ViewGroupMoveHandler.AcceptsMoves(new PlayerState { MapLoaded = false }));
        Assert.False(ViewGroupMoveHandler.AcceptsMoves(new PlayerState { MapLoaded = true, PendingGliderDismount = true }));
        Assert.True(ViewGroupMoveHandler.AcceptsMoves(new PlayerState { MapLoaded = true }));
    }

    private static byte[] Move1526(int length, byte marker)
    {
        var p = new byte[length];
        void F(int off, float v) { if (off + 4 <= length) BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(off), v); }
        F(40, 100f); F(44, 200f); F(48, 5f);      // current
        F(52, 100f); F(56, 200f); F(60, 5f);      // destination == current: standing still
        if (length > 90) p[90] = marker;           // inside the relayed slice [4..^4] of a 97-byte frame
        return p;
    }

    [Fact]
    public async Task Mv1_OnlyA97ByteFrameFromALoadedMapIsRelayed()
    {
        await using var net = await Harness.StartAsync(2);
        var players = new PlayerStateManager(new NoDirtySink());
        var mover = new PlayerState
        {
            CharacterId = 1, SessionId = net.Sessions[0].SessionId, EntityId = 0x11u, MapId = 1100,
            X = 100f, Y = 200f, Z = 5f, MapLoaded = true,
        };
        var observer = new PlayerState { CharacterId = 2, SessionId = net.Sessions[1].SessionId, EntityId = 0x21u, MapId = 1100 };
        players.AddPlayer(mover.SessionId, mover);
        players.AddPlayer(observer.SessionId, observer);
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(net.Server);
        var scopes = new ServiceCollection()
            .AddSingleton(new GameDataStore())
            .AddSingleton(new MovementGuard(new SystemClock()))
            .BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var handler = new ViewGroupMoveHandler(scopes, NullLogger<ViewGroupMoveHandler>.Instance, players, broadcast);
        var session = net.Sessions[0];

        // Wrong lengths: nothing relayed (60 was the old floor; 96/98 are one byte off the canonical 97).
        foreach (var len in new[] { 60, 96, 98 })
            await handler.HandleAsync(session, Move1526(len, 0xEE));

        await handler.HandleAsync(session, Move1526(97, 0xA1));                 // relayed

        mover.PendingGliderDismount = true;                                      // glider re-entry window: dropped
        await handler.HandleAsync(session, Move1526(97, 0xB2));
        mover.PendingGliderDismount = false;
        mover.MapLoaded = false;                                                 // before the 1304: dropped
        await handler.HandleAsync(session, Move1526(97, 0xB3));
        mover.MapLoaded = true;

        await handler.HandleAsync(session, Move1526(97, 0xC4));                 // relayed

        var first = await net.Readers[1].NextAsync();
        var second = await net.Readers[1].NextAsync();
        Assert.Equal(ZoneS2COpcodes.ViewGroupPCMoveTo, first.Op);
        Assert.Equal(93, first.Body.Length);
        Assert.Equal(0xA1, first.Body[90]);
        Assert.Equal(ZoneS2COpcodes.ViewGroupPCMoveTo, second.Op);
        Assert.Equal(93, second.Body.Length);
        Assert.Equal(0xC4, second.Body[90]);   // so the 60/96/98 and both gated 97s produced no frame in between
    }

    // ============================== CB-H9-refine: 1668 ==============================

    private const uint CharId = 910001u;
    private const uint Potion = 41000001;     // Equip_Slot 0: not an equip
    private const uint Armour = 41000002;     // Equip_Slot 4
    private const uint Powder = 41000003;     // a known REFINE_ITEM catalyst (all-zero row)
    private const long StartZeny = 1000;

    private sealed class RefineRig : IAsyncDisposable
    {
        public required Harness Net { get; init; }
        public required EconomyFixture Fx { get; init; }
        public required PlayerState Player { get; init; }
        public required RefineItemHandler Handler { get; init; }
        public ValueTask DisposeAsync() => Net.DisposeAsync();
    }

    private static async Task<RefineRig> StartRefineAsync()
    {
        var net = await Harness.StartAsync(1);
        var fx = new EconomyFixture();
        var store = new GameDataStore();
        void Set(string prop, object value) => typeof(GameDataStore).GetProperty(prop)!.SetValue(store, value);
        Set(nameof(GameDataStore.Items), new[]
        {
            new ItemData { ID = Potion, Name = "Potion", Stack_Max = 99 },
            new ItemData { ID = Armour, Name = "Armour", Equip_Slot = 4 },
            new ItemData { ID = Powder, Name = "Powder", Stack_Max = 99 },
        }.ToDictionary(t => t.ID).ToFrozenDictionary());
        Set(nameof(GameDataStore.REFINERATE), new[] { new REFINERATE { INDEX = 1, Need_Gold = 100, Succ_Rate = 10000 } });
        Set(nameof(GameDataStore.REFINEITEM), new[] { new REFINEITEM { ITEM_ID = Powder } });
        var player = fx.AddPlayer(CharId, StartZeny);
        player.SessionId = net.Sessions[0].SessionId;
        player.EntityId = 0x11u;
        var players = new PlayerStateManager(new NoDirtySink());
        players.AddPlayer(player.SessionId, player);
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(net.Server);
        var scopes = new ServiceCollection()
            .AddSingleton(fx.InventoryRepo)
            .AddSingleton(fx.CharacterRepo)
            .AddSingleton(store)
            .BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new RefineRig
        {
            Net = net, Fx = fx, Player = player,
            Handler = new RefineItemHandler(scopes, NullLogger<RefineItemHandler>.Instance, players, broadcast),
        };
    }

    /// <summary>1668: [01][len 50][50-byte blob] with equip uid at blob+20, material at +31, third at +42.</summary>
    private static byte[] Refine1668(ulong equipUid, ulong matUid, ulong thirdUid = 0)
    {
        var p = new byte[2 + 50];
        p[0] = 1; p[1] = 50;
        var blob = p.AsSpan(2);
        BinaryPrimitives.WriteUInt16LittleEndian(blob, 50);
        BinaryPrimitives.WriteUInt64LittleEndian(blob[20..], equipUid);
        BinaryPrimitives.WriteUInt64LittleEndian(blob[31..], matUid);
        BinaryPrimitives.WriteUInt64LittleEndian(blob[42..], thirdUid);
        return p;
    }

    /// <summary>The 2997 result code (blob+8) the client gets, skipping the other frames of a refine.</summary>
    private static async Task<ushort> ResultOf2997Async(RefineRig rig)
    {
        while (true)
        {
            var (op, body) = await rig.Net.Readers[0].NextAsync();
            if (op != ZoneS2COpcodes.OnRefineItem) continue;
            Assert.True(body.AsSpan().TryGetCompactBlob(CompactLengthCheck.Lenient, out var blob));
            return BinaryPrimitives.ReadUInt16LittleEndian(blob[8..]);
        }
    }

    private static List<string> Shape(EconomyFixture fx)
        => fx.Inventory.Rows.OrderBy(r => r.Id)
            .Select(r => $"{r.Id}:{r.ItemId}:x{r.Count}:st{r.StorageType}:slot{r.Slot}:+{r.Refine}").ToList();

    [Fact]
    public async Task Refine_ControlA_ArmourWithABagCatalystIsRefined()
    {
        // Positive control: proves the refusals below are caused by the new rules, not by an earlier reject.
        await using var rig = await StartRefineAsync();
        var armour = rig.Fx.GiveItem(CharId, Armour);
        var powder = rig.Fx.GiveItem(CharId, Powder, count: 2);

        await rig.Handler.HandleAsync(rig.Net.Sessions[0], Refine1668(armour.Id, powder.Id));

        Assert.Equal((ushort)0, await ResultOf2997Async(rig));
        Assert.Equal(StartZeny - 100, rig.Player.Gold);
    }

    [Fact]
    public async Task Refine_APotionIsRefusedAndNothingIsConsumed()
    {
        await using var rig = await StartRefineAsync();
        var potion = rig.Fx.GiveItem(CharId, Potion, count: 5);
        var powder = rig.Fx.GiveItem(CharId, Powder, count: 2);
        var before = Shape(rig.Fx);

        await rig.Handler.HandleAsync(rig.Net.Sessions[0], Refine1668(potion.Id, powder.Id));

        Assert.Equal((ushort)1, await ResultOf2997Async(rig));
        Assert.Equal(StartZeny, rig.Player.Gold);
        Assert.Equal(before, Shape(rig.Fx));
    }

    [Fact]
    public async Task Refine_AWornCatalystIsRefusedAndNothingIsConsumed()
    {
        await using var rig = await StartRefineAsync();
        var armour = rig.Fx.GiveItem(CharId, Armour);
        var worn = rig.Fx.GiveItem(CharId, Powder, count: 2, storageType: 1, slot: 3);
        var before = Shape(rig.Fx);

        await rig.Handler.HandleAsync(rig.Net.Sessions[0], Refine1668(armour.Id, worn.Id));

        Assert.Equal((ushort)1, await ResultOf2997Async(rig));
        Assert.Equal(StartZeny, rig.Player.Gold);
        Assert.Equal(before, Shape(rig.Fx));
    }

    [Fact]
    public async Task Refine_ABankedThirdPowderIsRefusedAndNothingIsConsumed()
    {
        await using var rig = await StartRefineAsync();
        var armour = rig.Fx.GiveItem(CharId, Armour);
        var powder = rig.Fx.GiveItem(CharId, Powder, count: 2);
        var banked = rig.Fx.GiveItem(CharId, Powder, count: 2, storageType: 2, slot: 0);
        var before = Shape(rig.Fx);

        await rig.Handler.HandleAsync(rig.Net.Sessions[0], Refine1668(armour.Id, powder.Id, banked.Id));

        Assert.Equal((ushort)1, await ResultOf2997Async(rig));
        Assert.Equal(StartZeny, rig.Player.Gold);
        Assert.Equal(before, Shape(rig.Fx));
    }

    // ============================== CB-H11: 1565 fall magnitude ==============================

    [Fact]
    public void Fall_NonFiniteOrNegativeIsRejectedAndHugeIsClamped()
    {
        Assert.Equal(0, FallDamagePackets.RetailFallDamage(float.PositiveInfinity, 66293));
        Assert.Equal(0, FallDamagePackets.RetailFallDamage(float.NegativeInfinity, 66293));
        Assert.Equal(0, FallDamagePackets.RetailFallDamage(float.NaN, 66293));
        Assert.Equal(0, FallDamagePackets.RetailFallDamage(-50f, 66293));
        // A finite absurd magnitude is clamped to the ceiling, not charged as 3e38 * maxHp.
        Assert.Equal(FallDamagePackets.RetailFallDamage(FallDamagePackets.MaxFallMagnitude, 66293),
            FallDamagePackets.RetailFallDamage(3e38f, 66293));
        // The largest legitimate fall magnitude is unaffected.
        Assert.True(150.4f < FallDamagePackets.MaxFallMagnitude);
        Assert.Equal((int)Math.Round(99899 * (45.46 - 10) * 0.05, MidpointRounding.ToEven),
            FallDamagePackets.RetailFallDamage(45.46f, 99899));

        Assert.False(FallDamagePackets.TryGuardMagnitude(float.PositiveInfinity, out _));
        Assert.False(FallDamagePackets.TryGuardMagnitude(-0.5f, out _));
        Assert.True(FallDamagePackets.TryGuardMagnitude(15.7f, out var kept));
        Assert.Equal(15.7f, kept);
    }

    [Fact]
    public async Task Fall_AForgedInfinityDoesNotKillTheFaller()
    {
        await using var net = await Harness.StartAsync(1);
        var players = new PlayerStateManager(new NoDirtySink());
        var player = new PlayerState
        {
            CharacterId = 1, SessionId = net.Sessions[0].SessionId, EntityId = 0x11u, MapId = 1100,
            Hp = 5000, MaxHp = 5000,
        };
        players.AddPlayer(player.SessionId, player);
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(net.Server);
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var handler = new FallDamageHandler(scopes, NullLogger<FallDamageHandler>.Instance, players, broadcast);

        await handler.HandleAsync(net.Sessions[0], BitConverter.GetBytes(float.PositiveInfinity));
        Assert.False(player.IsDead);
        Assert.Equal(5000, player.Hp);

        // A harmless 5.0 fall afterwards is answered with a 2815 of damage 0: it is the first frame the client gets,
        // so the +Inf one produced neither a death sequence nor a 2815 of its own.
        await handler.HandleAsync(net.Sessions[0], BitConverter.GetBytes(5f));
        var (op, body) = await net.Readers[0].NextAsync();
        Assert.Equal(ZoneS2COpcodes.NfyFallDamage, op);
        Assert.Equal(5000, player.Hp);
    }

    // ============================== CB-H12: 1682 defensive Equip_Slot rule ==============================

    private static async Task<GameDataStore> TransferStoreAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ro2-hsvd-transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // 60000758 ticket; two potions (Equip_Slot 0, no CardEnforceInfo row), two armour pieces (Equip_Slot 4 / 1),
            // two cards of one EnforceID (Equip_Slot 0, retail's card-onto-card case).
            await File.WriteAllTextAsync(Path.Combine(dir, "ItemInfo.json"), """
                [{"ID":60000758,"Item_Type":118},
                 {"ID":21110001,"Item_Type":3,"Grade":4},{"ID":21110002,"Item_Type":3,"Grade":4},
                 {"ID":16702100,"Item_Type":2,"Grade":4,"Equip_Slot":4},{"ID":16702158,"Item_Type":2,"Grade":4,"Equip_Slot":1},
                 {"ID":22310796,"Item_Type":13,"Grade":4},{"ID":22310832,"Item_Type":13,"Grade":4}]
                """);
            await File.WriteAllTextAsync(Path.Combine(dir, "REFINE_Trans_ITEM.json"), """
                [{"ITEM_ID":60000758,"Succ_Rate":20000,"DISC_RATE":10000,"Max_Value":10000,"limit_level":25}]
                """);
            await File.WriteAllTextAsync(Path.Combine(dir, "CardEnforceInfo.json"), """
                [{"Item_ID":22310796,"EnforceID":450},{"Item_ID":22310832,"EnforceID":450}]
                """);
            await File.WriteAllTextAsync(Path.Combine(dir, "Config_Def.json"), """
                [{"type":83,"Value":"5000"},{"type":107,"Value":"300"},{"type":108,"Value":"0.5"}]
                """);
            var store = new GameDataStore();
            await store.LoadAsync(dir);
            return store;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static InventoryItem Bag(ulong uid, uint itemId, byte grade) =>
        new() { Id = uid, CharacterId = 7, ItemId = itemId, Count = 1, StorageType = 0, Slot = 60, Refine = grade, Durability = 10000 };

    [Fact]
    public async Task Transfer_NonCardPairOutsideEquipSlot1To7IsRefused_ButCardsAndArmourPass()
    {
        var store = await TransferStoreAsync();
        static int? Lv(uint _) => 1;
        var ticket = Bag(1, 60000758, 0);

        // Armour onto armour passes (control: same grades and levels as the refused potion pair).
        Assert.Null(TransferReinforceItemHandler.Check(store, Lv, 7, ticket, Bag(2, 16702158, 0), Bag(3, 16702100, 10), out _));
        // Retail's card-onto-card case (Equip_Slot 0, one EnforceID) is exempt.
        Assert.Null(TransferReinforceItemHandler.Check(store, Lv, 7, ticket, Bag(2, 22310832, 0), Bag(3, 22310796, 10), out _));
        // Two potions (Equip_Slot 0, no card row) are refused by the flagged defensive rule.
        var why = TransferReinforceItemHandler.Check(store, Lv, 7, ticket, Bag(2, 21110002, 0), Bag(3, 21110001, 10), out _);
        Assert.NotNull(why);
        Assert.Contains("Equip_Slot", why);
    }
}
