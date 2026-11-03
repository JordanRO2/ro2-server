using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Collections.Domain;
using RO2.GameData;
using RO2.GameData.Tables;
using RO2.Inventory.Domain;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Tests.Economy;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.CollectionCharacterization;

/// <summary>
/// B10 S1 data: a tiny synthetic GameData that the tests control, so every expected value below traces to a row written here and
/// to the current code, not to the shipped tables. Cells: bag 1 slot 50 is flat bag slot 0, slot 51 is flat 1, ...
/// </summary>
internal static class CollData
{
    // ---- monster collections
    public const uint Mon = 20001, MonSingle = 20002, MonNoRecipe = 20003, MonBigDna = 20004;
    public const uint Dna = 9001, Protein = 9002, PieceA = 9011, PieceB = 9012, BigDna = 9021, Filler = 9099;
    public const uint Skill1 = 5101, Skill2 = 5102, Skill3 = 5103, Skill4 = 5104;

    // ---- pets
    public const uint Pet = 83000001, PetBig = 83000002, PetNoRecipe = 83000009, PetUnknown = 83999999;
    public const uint PetEgg = 7001, PetHatched = 7002, Catalyst = 7010, PetPieceA = 7011, PetPieceB = 7012;
    public const uint Incubator = 7101, IncubatorBig = 7102;
    public const ushort PetCap = 25, PetBigCap = 100;
    public const uint PetEffect0 = 111, PetEffect1 = 222;

    public static CollectMonster[] MonsterRows() =>
    [
        // 20001: four levels, Exp = the DNA uses banked toward the NEXT level. The recipe (read from level 1): protein x2 per combine,
        // pieces A x2 + B x1 + A x1 (Piece4 repeats Piece1's item, so the need is grouped to 3 x A per combine).
        Row(Mon, 1, Skill1, exp: 2), Row(Mon, 2, Skill2, exp: 3), Row(Mon, 3, Skill3, exp: 1), Row(Mon, 4, Skill4, exp: 1),
        Row(MonSingle, 1, 5201, exp: 5),                       // a one-level collection: MaxLevel 1
        Row(MonNoRecipe, 1, 5301, exp: 5, result: 0),          // no DNA item result: 1622 refuses
        Row(MonBigDna, 1, 5401, exp: 5, result: BigDna),
    ];

    private static CollectMonster Row(uint npc, byte level, uint skill, ushort exp, uint? result = null) => new()
    {
        NpcID = npc, SummonLevel = level, SkillID = skill, Exp = exp,
        ItemID_Piece1 = PieceA, Num_Piece1 = 2, ItemID_Piece2 = PieceB, Num_Piece2 = 1, ItemID_Piece3 = 0, Num_Piece3 = 0,
        ItemID_Piece4 = PieceA, Num_Piece4 = 1,
        ItemID_Combine1 = Protein, Num_Combine1 = 2,
        ItemID_Result = result ?? (npc == MonBigDna ? BigDna : Dna),
    };

    public static CollectRate[] RateRows() =>
    [
        new() { Index = 1, Num_Start = 0, Num_End = 2, Skill_Max = 3, Skill_Cooltime = 1000 },
        new() { Index = 2, Num_Start = 3, Num_End = 5, Skill_Max = 4, Skill_Cooltime = 2000 },
        new() { Index = 3, Num_Start = 6, Num_End = 9, Skill_Max = 5, Skill_Cooltime = 3000 },
    ];

    public static PetInfo[] PetRows() =>
    [
        new()
        {
            NPC_ID = Pet, Pet_Egg_ID = PetEgg, Hatched_Egg_ID = PetHatched, Incubation_Time = 600,
            ItemID_Catalyst = Catalyst, Num_Catalyst = 1, ItemID_Piece1 = PetPieceA, Num_Piece1 = 2, ItemID_Piece2 = PetPieceB, Num_Piece2 = 1,
            LoyaltyLevel_1 = PetCap, LoyaltyEffect_0 = PetEffect0, LoyaltyEffect_1 = PetEffect1,
        },
        new() { NPC_ID = PetBig, Pet_Egg_ID = 7003, Hatched_Egg_ID = 7004, Incubation_Time = 100, LoyaltyLevel_1 = PetBigCap, LoyaltyEffect_0 = 11, LoyaltyEffect_1 = 22 },
        new() { NPC_ID = PetNoRecipe, Pet_Egg_ID = 0, Hatched_Egg_ID = 7006, LoyaltyLevel_1 = 50, LoyaltyEffect_0 = 33, LoyaltyEffect_1 = 44 },
    ];

    public static IncubatorInfo[] IncubatorRows() =>
    [
        new() { Incubator_ID = Incubator, Charge_Quantity = 2, Incubation_Speed = 10000 },
        new() { Incubator_ID = IncubatorBig, Charge_Quantity = 1, Incubation_Speed = 5000 },
    ];

    public static ItemData[] ItemRows() =>
    [
        new() { ID = Dna, Stack_Max = 99 }, new() { ID = Protein, Stack_Max = 99 }, new() { ID = PieceA, Stack_Max = 99 },
        new() { ID = PieceB, Stack_Max = 99 }, new() { ID = BigDna, Stack_Max = 1 }, new() { ID = Filler, Stack_Max = 1 },
        new() { ID = PetEgg, Stack_Max = 99 }, new() { ID = PetHatched, Stack_Max = 99 }, new() { ID = Catalyst, Stack_Max = 99 },
        new() { ID = PetPieceA, Stack_Max = 99 }, new() { ID = PetPieceB, Stack_Max = 99 }, new() { ID = Incubator, Stack_Max = 1 },
    ];

    public static async Task<GameDataStore> StoreAsync(CollectRate[]? rates = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ro2-b10s1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "CollectMonster.json"), JsonSerializer.Serialize(MonsterRows()));
            await File.WriteAllTextAsync(Path.Combine(dir, "CollectRate.json"), JsonSerializer.Serialize(rates ?? RateRows()));
            await File.WriteAllTextAsync(Path.Combine(dir, "PetInfo.json"), JsonSerializer.Serialize(PetRows()));
            await File.WriteAllTextAsync(Path.Combine(dir, "IncubatorInfo.json"), JsonSerializer.Serialize(IncubatorRows()));
            await File.WriteAllTextAsync(Path.Combine(dir, "ItemInfo.json"), JsonSerializer.Serialize(ItemRows()));
            var store = new GameDataStore();
            await store.LoadAsync(dir);
            return store;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>The client cell (bag 1, slot 50 + flat) of a flat main-bag slot 0..19.</summary>
    public static byte CellOf(int flat) => (byte)(50 + flat);
}

/// <summary>An in-memory <see cref="ICollectionRepository"/> with the Mongo repository's copy semantics (a load returns a fresh object).</summary>
internal sealed class FakeCollectionRepo : ICollectionRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<uint, CollectionDocument> _docs = [];
    private TaskCompletionSource? _release;
    private int _waiting, _barrier;

    public int Saves { get; private set; }
    public bool ThrowOnGet { get; set; }
    public bool ThrowOnSave { get; set; }

    public CollectionDocument? Stored(uint id) { lock (_gate) return _docs.TryGetValue(id, out var d) ? Clone(d) : null; }

    public void Seed(CollectionDocument d) { lock (_gate) _docs[d.Id] = Clone(d); }

    /// <summary>Makes the next <paramref name="n"/> loads wait until all n have been served (each gets its own copy), so n requests read
    /// the same stored state before any of them writes: the lost-update interleaving of a non-atomic read-modify-write.</summary>
    public void HoldLoadsUntil(int n) { _barrier = n; _waiting = 0; _release = new(TaskCreationOptions.RunContinuationsAsynchronously); }

    public async Task<CollectionDocument> GetOrCreateAsync(uint characterId, CancellationToken ct = default)
    {
        if (ThrowOnGet) throw new IOException("injected load failure");
        CollectionDocument doc;
        lock (_gate) doc = _docs.TryGetValue(characterId, out var d) ? Clone(d) : new CollectionDocument { Id = characterId };
        var release = _release;
        if (release is not null && Interlocked.Increment(ref _waiting) >= _barrier) release.TrySetResult();
        if (release is not null) await release.Task.WaitAsync(Generous, ct);
        return doc;
    }

    public Task SaveAsync(CollectionDocument doc, CancellationToken ct = default)
    {
        if (ThrowOnSave) throw new IOException("injected save failure");
        lock (_gate) { _docs[doc.Id] = Clone(doc); Saves++; }
        return Task.CompletedTask;
    }

    private static CollectionDocument Clone(CollectionDocument d) => new()
    {
        Id = d.Id, RegisteredMonsterIds = [.. d.RegisteredMonsterIds], RegisteredPetIds = [.. d.RegisteredPetIds],
        SelectedMonsterSkills = [.. d.SelectedMonsterSkills], SelectedPetSkills = [.. d.SelectedPetSkills],
        ActivePetId = d.ActivePetId, ActivePetFlag = d.ActivePetFlag,
        PetLoyalties = [.. d.PetLoyalties.Select(p => new PetLoyalty { PetId = p.PetId, Loyalty = p.Loyalty })],
    };
}

internal sealed class FakeMonsterProgressRepo : IMonsterCollectionProgressRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<uint, MonsterCollectionProgressDocument> _docs = [];
    public bool ThrowOnGet { get; set; }
    public bool ThrowOnSave { get; set; }

    public MonsterCollectionProgressDocument? Stored(uint id) { lock (_gate) return _docs.TryGetValue(id, out var d) ? Clone(d) : null; }
    public void Seed(MonsterCollectionProgressDocument d) { lock (_gate) _docs[d.Id] = Clone(d); }

    public Task<MonsterCollectionProgressDocument?> GetAsync(uint characterId, CancellationToken ct = default)
    {
        if (ThrowOnGet) throw new IOException("injected load failure");
        lock (_gate) return Task.FromResult(_docs.TryGetValue(characterId, out var d) ? Clone(d) : null);
    }

    public Task SaveAsync(MonsterCollectionProgressDocument doc, CancellationToken ct = default)
    {
        if (ThrowOnSave) throw new IOException("injected save failure");
        lock (_gate) _docs[doc.Id] = Clone(doc);
        return Task.CompletedTask;
    }

    private static MonsterCollectionProgressDocument Clone(MonsterCollectionProgressDocument d) => new()
    {
        Id = d.Id, Entries = [.. d.Entries.Select(e => new MonsterCollectionEntry { NpcId = e.NpcId, Level = e.Level, Exp = e.Exp })],
    };
}

/// <summary>An <see cref="IPetIncubatorRepository"/> that does what the Mongo/Postgres ones do: load the list, run the Domain rule, store the list.</summary>
internal sealed class FakeIncubatorRepo : IPetIncubatorRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<uint, List<PetIncubatorSlot>> _lists = [];
    public bool ThrowOnGet { get; set; }
    public bool ThrowOnWrite { get; set; }

    public List<PetIncubatorSlot> Stored(uint id) { lock (_gate) return Load(id); }
    public void Seed(uint id, params PetIncubatorSlot[] slots) { lock (_gate) _lists[id] = [.. slots.Select(PetIncubatorRules.Copy)]; }

    private List<PetIncubatorSlot> Load(uint id) => _lists.TryGetValue(id, out var l) ? [.. l.Select(PetIncubatorRules.Copy)] : [];

    public Task<IReadOnlyList<PetIncubatorSlot>> GetSlotsAsync(uint characterId, CancellationToken ct = default)
    {
        if (ThrowOnGet) throw new IOException("injected load failure");
        lock (_gate) return Task.FromResult<IReadOnlyList<PetIncubatorSlot>>(Load(characterId));
    }

    private Task<T> Edit<T>(uint id, Func<List<PetIncubatorSlot>, (bool store, T result)> f)
    {
        if (ThrowOnWrite) throw new IOException("injected write failure");
        lock (_gate)
        {
            var list = Load(id);
            var (store, result) = f(list);
            if (store) _lists[id] = list;
            return Task.FromResult(result);
        }
    }

    public Task<bool> TryPlaceIncubatorAsync(uint characterId, byte slot, uint incubatorId, byte charges, DateTime nowUtc, CancellationToken ct = default)
        => Edit(characterId, l => { var ok = PetIncubatorRules.TryPlace(l, slot, incubatorId, charges, nowUtc); return (ok, ok); });

    public Task<bool> TryStartIncubationAsync(uint characterId, byte slot, uint incubatorId, uint eggId, DateTime startUtc, DateTime endUtc, CancellationToken ct = default)
        => Edit(characterId, l => { var ok = PetIncubatorRules.TryStartIncubation(l, slot, incubatorId, eggId, startUtc, endUtc); return (ok, ok); });

    public Task<PetIncubatorSlot?> TryCollectAsync(uint characterId, byte slot, uint incubatorId, DateTime nowUtc, CancellationToken ct = default)
        => Edit<PetIncubatorSlot?>(characterId, l =>
        {
            var s = PetIncubatorRules.CollectableSlot(l, slot, incubatorId, nowUtc);
            if (s == null) return (false, null);
            var before = PetIncubatorRules.Copy(s);
            PetIncubatorRules.ApplyCollect(l, s, nowUtc);
            return (true, before);
        });

    public Task RestoreSlotAsync(uint characterId, PetIncubatorSlot before, CancellationToken ct = default)
        => Edit(characterId, l => { PetIncubatorRules.RestoreSlot(l, before); return (true, true) ; });
}

/// <summary>Records the quest hook the collect handler fires; the collect-quest engine itself is not under test.</summary>
internal sealed class RecordingQuestEvents : IQuestObjectiveEvents
{
    public List<(uint ItemId, int Count)> Gained { get; } = [];
    public Task OnItemGainedAsync(ProudSession session, PlayerState player, uint itemId, int count) { Gained.Add((itemId, count)); return Task.CompletedTask; }
    public Task OnItemLeftBagAsync(ProudSession session, PlayerState player, uint itemId, IServiceProvider services, ILogger log, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// B10 S1 rig: the real Collection handlers and services over a loopback ProudNet session, with in-memory repositories (the Economy
/// inventory fake and the three Collections fakes). The tests pin what the host does TODAY (quirks included) so that a byte-identical
/// move of the Collections code keeps them green.
/// </summary>
internal sealed class CollRig : IAsyncDisposable
{
    public const uint Self = 1;
    public const uint SelfHandle = 0x07087E11;

    public required Harness Net { get; init; }
    public required EconomyFixture Fx { get; init; }
    public required PlayerStateManager Players { get; init; }
    public required BroadcastService Broadcast { get; init; }
    public required ServiceProvider Services { get; init; }
    public required PlayerState Player { get; init; }
    public required GameDataStore Store { get; init; }
    public required FakeCollectionRepo Book { get; init; }
    public required FakeMonsterProgressRepo Progress { get; init; }
    public required FakeIncubatorRepo Incubators { get; init; }
    public required RecordingQuestEvents Quest { get; init; }
    public ProudSession Session => Net.Sessions[0];
    public IServiceScopeFactory Scopes => Services.GetRequiredService<IServiceScopeFactory>();
    public CollHandlers Handlers => new(this);

    public static async Task<CollRig> StartAsync()
    {
        var store = await CollData.StoreAsync();
        var fx = new EconomyFixture();
        fx.AddOfflineCharacter(Self, 0, name: "Collector");
        var net = await Harness.StartAsync(1);
        var players = new PlayerStateManager(new NoDirtySink());
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(net.Server);
        var player = new PlayerState
        {
            SessionId = net.Sessions[0].SessionId, EntityId = SelfHandle, CharacterId = Self, Name = "Collector",
            MapId = 1100, X = 10, Y = 10, MapLoaded = true,
        };
        players.AddPlayer(player.SessionId, player);
        var book = new FakeCollectionRepo();
        var progress = new FakeMonsterProgressRepo();
        var incubators = new FakeIncubatorRepo();
        var quest = new RecordingQuestEvents();
        var services = new ServiceCollection()
            .AddSingleton(store).AddSingleton(fx.InventoryRepo).AddSingleton<ICollectionRepository>(book)
            .AddSingleton<IMonsterCollectionProgressRepository>(progress).AddSingleton<IPetIncubatorRepository>(incubators)
            .AddSingleton<IQuestObjectiveEvents>(quest)
            .BuildServiceProvider();
        return new CollRig
        {
            Net = net, Fx = fx, Players = players, Broadcast = broadcast, Services = services, Player = player, Store = store,
            Book = book, Progress = progress, Incubators = incubators, Quest = quest,
        };
    }

    /// <summary>Every frame left for the client since the previous drain, in order.</summary>
    public async Task<List<(ushort Op, byte[] Body)>> DrainAsync()
    {
        await Session.SendInternalRmiAsync(ZoneS2COpcodes.OnLevelUp, new byte[] { 0x5A });
        var frames = new List<(ushort, byte[])>();
        while (true)
        {
            var frame = await Net.Readers[0].NextAsync();
            if (frame.Op == ZoneS2COpcodes.OnLevelUp) return frames;
            frames.Add(frame);
        }
    }

    /// <summary>Sends a request through the real handler and returns every frame it left for the sender.</summary>
    public async Task<List<(ushort Op, byte[] Body)>> CallAsync(RO2.Network.Handlers.IRmiHandler handler, byte[] request)
    {
        await handler.HandleAsync(Session, request);
        return await DrainAsync();
    }

    public ValueTask DisposeAsync()
    {
        Services.Dispose();
        return Net.DisposeAsync();
    }

    // ---- request builders (the C2S bodies the handlers parse): [bag][slot][times][u32 id] ----------------------------------

    public static byte[] Cell(byte cell, byte times, uint id, byte bag = 1)
    {
        var b = new byte[7];
        b[0] = bag; b[1] = cell; b[2] = times;
        BitConverter.GetBytes(id).CopyTo(b, 3);
        return b;
    }

    public static byte[] Id(uint id) => BitConverter.GetBytes(id);

    /// <summary>Puts a stack in a flat main-bag cell and returns the stored row.</summary>
    public InventoryItem Give(uint itemId, int count, int flat) => Fx.GiveItem(Self, itemId, count, slot: (short)flat);

    public InventoryItem? Cellrow(int flat) => Fx.Inventory.Rows.SingleOrDefault(r => r.CharacterId == Self && r.StorageType == 0 && r.Slot == flat);

    public int CountOf(uint itemId) => Fx.Inventory.Rows.Where(r => r.CharacterId == Self && r.StorageType == 0 && r.ItemId == itemId).Sum(r => r.Count);

    public (ushort Op, byte[] Body) Use2329(InventoryItem item, int flat, int remaining)
        => (ZoneS2COpcodes.OnUseItem, CombatPackets.BuildUseItem2329(SelfHandle, CollData.CellOf(flat), 1, item.Id, (byte)remaining, 3));

    public static string[] Ops(IEnumerable<(ushort Op, byte[] Body)> frames) => [.. frames.Select(f => f.Op.ToString())];
}

/// <summary>The real handlers of the Collections cluster, built on a <see cref="CollRig"/>.</summary>
internal readonly struct CollHandlers(CollRig r)
{
    public RO2.Server.Zone.Handlers.Rmi.MCollectionHandler Mon_1620 => new(r.Scopes, NullLogger<RO2.Server.Zone.Handlers.Rmi.MCollectionHandler>.Instance, r.Players, r.Broadcast);
    public RO2.Server.Zone.Handlers.Rmi.MCollectionSelectedHandler Mon_1621 => new(r.Scopes, NullLogger<RO2.Server.Zone.Handlers.Rmi.MCollectionSelectedHandler>.Instance, r.Players, r.Broadcast);
    public RO2.Server.Zone.Handlers.Rmi.MCollectionCombinProteinHandler Mon_1622 => new(r.Scopes, NullLogger<RO2.Server.Zone.Handlers.Rmi.MCollectionCombinProteinHandler>.Instance, r.Players, r.Broadcast);
    public RO2.Server.Zone.Handlers.Rmi.MCollectionUseProteinHandler Mon_1623 => new(r.Scopes, NullLogger<RO2.Server.Zone.Handlers.Rmi.MCollectionUseProteinHandler>.Instance, r.Players, r.Broadcast);
    public RO2.Server.Zone.Handlers.Rmi.PCollectionHandler Pet_1624 => new(r.Scopes, NullLogger<RO2.Server.Zone.Handlers.Rmi.PCollectionHandler>.Instance, r.Players, r.Broadcast);
    public RO2.Server.Zone.Handlers.Rmi.PCollectionSelectedHandler Pet_1625 => new(r.Scopes, NullLogger<RO2.Server.Zone.Handlers.Rmi.PCollectionSelectedHandler>.Instance, r.Players, r.Broadcast);
    public RO2.Server.Zone.Handlers.Rmi.PCollectionCombinEggBlankHandler Pet_1626 => new(r.Scopes, NullLogger<RO2.Server.Zone.Handlers.Rmi.PCollectionCombinEggBlankHandler>.Instance, r.Players, r.Broadcast);
    public RO2.Server.Zone.Handlers.Rmi.PCollectionUsePetEggHandler Pet_1627 => new(r.Scopes, NullLogger<RO2.Server.Zone.Handlers.Rmi.PCollectionUsePetEggHandler>.Instance, r.Players, r.Broadcast);
    public RO2.Server.Zone.Handlers.Rmi.PCollectionActivePetHandler Pet_1628 => new(r.Scopes, NullLogger<RO2.Server.Zone.Handlers.Rmi.PCollectionActivePetHandler>.Instance, r.Players, r.Broadcast);
    public RO2.Server.Zone.Handlers.Rmi.PCollectionSetActivePetHandler Pet_1629 => new(r.Scopes, NullLogger<RO2.Server.Zone.Handlers.Rmi.PCollectionSetActivePetHandler>.Instance, r.Players, r.Broadcast);
    public RO2.Server.Zone.Handlers.Rmi.PCollectionIncubatorHandler Pet_1630 => new(r.Scopes, NullLogger<RO2.Server.Zone.Handlers.Rmi.PCollectionIncubatorHandler>.Instance, r.Players, r.Broadcast);
    public RO2.Server.Zone.Handlers.Rmi.PCollectionEggIntoIncubatorHandler Pet_1631 => new(r.Scopes, NullLogger<RO2.Server.Zone.Handlers.Rmi.PCollectionEggIntoIncubatorHandler>.Instance, r.Players, r.Broadcast);
    public RO2.Server.Zone.Handlers.Rmi.PCollectionCollectEggHandler Pet_1632 => new(r.Scopes, NullLogger<RO2.Server.Zone.Handlers.Rmi.PCollectionCollectEggHandler>.Instance, r.Players, r.Broadcast);
}
