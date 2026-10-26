using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;
using RO2.Worlds.Domain;

namespace RO2.Tests.Services;

/// <summary>
/// Illusion Pyramid behavior: the reward catalog and odds, the draw rules, the 2907, 2908 and 2909 bodies,
/// and the unsolicited 2909 close when the player leaves.
/// </summary>
public class IllusionPyramidServiceTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    [Fact]
    public void CatalogHasRetailsFloorsAndOdds()
    {
        var rewards = IllusionPyramidCatalog.Rewards;
        Assert.Equal(42, rewards.Count);
        for (byte floor = 1; floor <= IllusionPyramidCatalog.TopFloor; floor++)
        {
            int slots = rewards.Count(r => r.Floor == floor);
            Assert.Equal(10 - floor, slots); 
            Assert.Equal(slots, IllusionPyramidCatalog.SlotWeights[floor - 1].Count);
        }
        Assert.All(rewards.Where(r => r.Floor == IllusionPyramidCatalog.TopFloor), r => Assert.True(r.End));
        Assert.All(rewards.Where(r => r.IsMileage), r => Assert.True(r.End)); 
        Assert.Equal(311_124, IllusionPyramidCatalog.SlotWeights.Sum(w => w.Sum()));

        var first = IllusionPyramidCatalog.Find(1, 1)!;
        Assert.Equal((30000267u, (byte)1, IllusionPyramidCatalog.MysticalMileageId, true), (first.ItemId, first.Count, first.MileageId, first.End));
    }

    [Fact]
    public void RollPicksSlotsByWeight()
    {
        
        Assert.Equal(1, IllusionPyramidCatalog.Roll(1, _ => 0).Slot);
        Assert.Equal(1, IllusionPyramidCatalog.Roll(1, _ => 11320).Slot);
        Assert.Equal(2, IllusionPyramidCatalog.Roll(1, _ => 11321).Slot);
        Assert.Equal(9, IllusionPyramidCatalog.Roll(1, n => n - 1).Slot);
        Assert.Equal(3, IllusionPyramidCatalog.Roll(7, n => n - 1).Slot);
    }

    [Fact]
    public void EntriesStartOnTheirFloor()
    {
        Assert.Equal(IllusionPyramidCatalog.EntryKind.PhantomStone, IllusionPyramidCatalog.KindOf(0x10, 60000377));
        Assert.Equal(IllusionPyramidCatalog.EntryKind.Mystical, IllusionPyramidCatalog.KindOf(0x11, 1));
        Assert.Equal(IllusionPyramidCatalog.EntryKind.Bright, IllusionPyramidCatalog.KindOf(0x11, 2));
        Assert.Equal(IllusionPyramidCatalog.EntryKind.None, IllusionPyramidCatalog.KindOf(0x10, 1));
        Assert.Equal(new byte[] { 1, 3, 5 }, new[]
        {
            IllusionPyramidCatalog.EntryFloor(IllusionPyramidCatalog.EntryKind.PhantomStone),
            IllusionPyramidCatalog.EntryFloor(IllusionPyramidCatalog.EntryKind.Mystical),
            IllusionPyramidCatalog.EntryFloor(IllusionPyramidCatalog.EntryKind.Bright),
        });
    }

 /// <summary>
 /// Replays a full pyramid session: every C2S draw, fetch and finish in order, with the 2907 code expected for
 /// each draw. Accepted draws receive the result the 2908 reports, so the end flags of the run follow it.
 /// </summary>
    [Fact]
    public void ReplaysARetailSessionCodeForCode()
    {
        var events = new (string Time, string Op, string Body, ushort? Retail, (byte Floor, byte Slot)? Result)[]
        {
            ("12:46:47.052", "1617", "01107979889303", 0, (1, 9)),      
            ("12:46:47.316", "1618", "", null, null),                   
            ("12:46:49.631", "1619", "", null, null),
            ("12:46:50.160", "1617", "01107979889303", 0, (1, 8)),
            ("12:46:50.409", "1618", "", null, null),
            ("12:46:52.794", "1617", "02137979889303", 0, (2, 7)),      
            ("12:46:53.061", "1618", "", null, null),
            ("12:46:53.348", "1617", "02137979889303", 29002, null),    
            ("12:46:55.434", "1617", "03137979889303", 0, (3, 3)),
            ("12:46:55.747", "1618", "", null, null),
            ("12:46:55.960", "1617", "03137979889303", 29002, null),
            ("12:46:58.275", "1617", "04137979889303", 0, (4, 6)),      
            ("12:46:58.607", "1618", "", null, null),
            ("12:46:58.814", "1617", "04137979889303", 29002, null),
            ("12:47:01.108", "1619", "", null, null),
            ("12:47:01.653", "1617", "01107979889303", 0, (1, 2)),
            ("12:47:01.918", "1618", "", null, null),
            ("12:47:02.198", "1617", "01107979889303", 29002, null),
            ("12:47:04.384", "1617", "02137979889303", 0, (2, 4)),
            ("12:47:04.684", "1618", "", null, null),
            ("12:47:04.951", "1617", "02137979889303", 29002, null),
            ("12:47:07.224", "1617", "02137979889303", 29010, null),    
            ("12:47:07.787", "1617", "03137979889303", 0, (3, 3)),
            ("12:47:08.011", "1618", "", null, null),
            ("12:47:10.410", "1617", "04137979889303", 0, (4, 3)),
            ("12:47:10.801", "1618", "", null, null),
            ("12:47:10.961", "1617", "04137979889303", 29002, null),
        };

        var slot = new IllusionPyramidRunStore.Slot();
        foreach (var e in events)
        {
            long t = (long)TimeSpan.Parse(e.Time).TotalMilliseconds;
            switch (e.Op)
            {
                case "1617":
                    Assert.True(IllusionPyramidService.TryParseDraw(Convert.FromHexString(e.Body), out var req));
                    ushort code = IllusionPyramidService.Check(slot, req, t);
                    Assert.True(e.Retail == code, $"{e.Time}: retail {e.Retail}, ours {code}");
                    if (code == 0)
                        IllusionPyramidService.Accept(slot, req, IllusionPyramidCatalog.Find(e.Result!.Value.Floor, e.Result.Value.Slot)!, t);
                    break;
                case "1618":
                    Assert.NotNull(IllusionPyramidService.Fetch(slot));
                    break;
                case "1619":
                    Assert.True(slot.DrewSinceFinish);
                    IllusionPyramidService.Close(slot);
                    break;
            }
        }
    }

    [Fact]
    public void RefusesWhatRetailRefuses()
    {
        var stoneStart = Draw("01107979889303");
        var slot = new IllusionPyramidRunStore.Slot();

        
        
        Assert.Equal(IllusionPyramidService.ResultWrongFloor, IllusionPyramidService.Check(slot, Draw("02137979889303"), 0));
        Assert.Equal(IllusionPyramidService.ResultAbnormalInformation, IllusionPyramidService.Check(slot, Draw("02107979889303"), 0));
        Assert.Equal(0, IllusionPyramidService.Check(slot, Draw("03117801000000"), 0));
        Assert.Equal(IllusionPyramidService.ResultAbnormalInformation, IllusionPyramidService.Check(slot, Draw("01117801000000"), 0));
        Assert.Equal(0, IllusionPyramidService.Check(slot, Draw("05117802000000"), 0));

        
        IllusionPyramidService.Accept(slot, stoneStart, IllusionPyramidCatalog.Find(1, 2)!, 0);
        Assert.Equal(IllusionPyramidService.ResultSpeedAbnormal, IllusionPyramidService.Check(slot, Draw("02137979889303"), 1_999));
        Assert.Equal(IllusionPyramidService.ResultAlreadyExploring, IllusionPyramidService.Check(slot, Draw("02137979889303"), 2_000));

        
        Assert.Equal(IllusionPyramidCatalog.Find(1, 2), IllusionPyramidService.Fetch(slot));
        Assert.Null(IllusionPyramidService.Fetch(slot)); 
        Assert.Equal(IllusionPyramidService.ResultWrongFloor, IllusionPyramidService.Check(slot, Draw("03137979889303"), 5_000));
        Assert.Equal(0, IllusionPyramidService.Check(slot, Draw("02137979889303"), 5_000));
        Assert.Equal(0, IllusionPyramidService.Check(slot, stoneStart, 5_000));

        
        IllusionPyramidService.Accept(slot, stoneStart, IllusionPyramidCatalog.Find(1, 9)!, 5_000);
        IllusionPyramidService.Fetch(slot);
        Assert.Equal(IllusionPyramidService.ResultCannotAdvance, IllusionPyramidService.Check(slot, Draw("02137979889303"), 9_000));
        Assert.Equal(IllusionPyramidService.ResultCannotAdvance, IllusionPyramidService.Check(slot, stoneStart, 9_000));
        IllusionPyramidService.Close(slot);
        Assert.Equal(0, IllusionPyramidService.Check(slot, stoneStart, 5_001));

        
        Assert.Equal(IllusionPyramidService.ResultAbnormalInformation, IllusionPyramidService.Check(slot, Draw("01127979889303"), 0));
    }

    [Fact]
    public void BodiesAreRetailsBytes()
    {
        
        
        Assert.Equal(Convert.FromHexString("000000015587930301010300"), IllusionPyramidService.Build2908(IllusionPyramidCatalog.Find(1, 3)!));
        Assert.Equal(Convert.FromHexString("000000000100000001010901"), IllusionPyramidService.Build2908(IllusionPyramidCatalog.Find(1, 9)!));
        Assert.Equal(Convert.FromHexString("4f7100000000000000000000"), IllusionPyramidService.Build2908NoResult());

        
        Assert.Equal(Convert.FromHexString("000000"), IllusionPyramidService.Build2907(0));
        Assert.Equal(Convert.FromHexString("4a7100"), IllusionPyramidService.Build2907(IllusionPyramidService.ResultSpeedAbnormal));
        Assert.Equal(Convert.FromHexString("527100"), IllusionPyramidService.Build2907(IllusionPyramidService.ResultWrongFloor));
        Assert.Equal(Convert.FromHexString("000000"), IllusionPyramidService.Build2909(0));
        Assert.Equal(Convert.FromHexString("4f7100"), IllusionPyramidService.Build2909(IllusionPyramidService.ResultNotExploring));
    }

    [Fact]
    public void MileageIsCappedAndTheTopRecordOnlyRises()
    {
        var state = new IllusionPyramidStateDocument { Mystical = 998, Bright = 5, TopRecord = 4 };
        IllusionPyramidService.ApplyMileage(state, IllusionPyramidCatalog.MysticalMileageId, 2);
        IllusionPyramidService.ApplyMileage(state, IllusionPyramidCatalog.BrightMileageId, 2);
        Assert.Equal((999, 7), (state.Mystical, state.Bright));

        Assert.False(IllusionPyramidService.RaiseTopRecord(state, 3));
        Assert.True(IllusionPyramidService.RaiseTopRecord(state, 6));
        Assert.Equal(6, state.TopRecord);
    }

 /// <summary>
 /// The unsolicited close is <c>[u16 code][u8 1]</c>. Only codes 0 and 29016 are sent by this server; the other
 /// codes check that the layout holds for every code.
 /// </summary>
    [Theory]
    [InlineData((ushort)0, "000001")]     
    [InlineData((ushort)29016, "587101")] 
    [InlineData((ushort)29005, "4d7101")] 
    [InlineData((ushort)29012, "547101")] 
    public void LeaveCloseIsRetailsBytes(ushort code, string retail)
    {
        Assert.Equal(Convert.FromHexString(retail), IllusionPyramidService.Build2909Close(code));
    }

 /// <summary>
 /// Runs the live entry points end to end: the last pyramid request, an item teleport's in-place 2311 with its
 /// 2909 close, a glider's 2938 that still gets <c>000001</c> with no request in between, and a 2938 that gets
 /// nothing. A new use arms it again.
 /// </summary>
    [Fact]
    public async Task LeaveClosesFollowARetailSession()
    {
        await using var rig = await Rig.StartAsync();
        var player = new PlayerState { EntityId = 0x0B2FF411u };
        var services = new ServiceCollection().BuildServiceProvider();
        var ct = CancellationToken.None;
        var marker = IllusionPyramidService.Build2907(0);

        
        await IllusionPyramidService.CloseOnDepartureAsync(rig.Session, player, services, NullLogger.Instance, ct);
        await rig.Session.SendRmiAsync(ZoneS2COpcodes.AnsIllusionPyramidDrawStage, marker, ct);
        Assert.Equal((ZoneS2COpcodes.AnsIllusionPyramidDrawStage, "000000"), await rig.NextRmiHexAsync());

        await IllusionPyramidService.NoteStateRequestAsync(player, ct); 

        await IllusionPyramidService.CloseOnMapLoadAsync(rig.Session, player, services, NullLogger.Instance, ct);
        Assert.Equal((ZoneS2COpcodes.AnsIllusionPyramidFinish, "587101"), await rig.NextRmiHexAsync());

        await IllusionPyramidService.CloseOnDepartureAsync(rig.Session, player, services, NullLogger.Instance, ct);
        Assert.Equal((ZoneS2COpcodes.AnsIllusionPyramidFinish, "000001"), await rig.NextRmiHexAsync());

        
        await IllusionPyramidService.CloseOnDepartureAsync(rig.Session, player, services, NullLogger.Instance, ct);
        await IllusionPyramidService.CloseOnMapLoadAsync(rig.Session, player, services, NullLogger.Instance, ct);
        await rig.Session.SendRmiAsync(ZoneS2COpcodes.AnsIllusionPyramidDrawStage, marker, ct);
        Assert.Equal((ZoneS2COpcodes.AnsIllusionPyramidDrawStage, "000000"), await rig.NextRmiHexAsync());

        await IllusionPyramidService.NoteStateRequestAsync(player, ct);
        await IllusionPyramidService.CloseOnDepartureAsync(rig.Session, player, services, NullLogger.Instance, ct);
        Assert.Equal((ZoneS2COpcodes.AnsIllusionPyramidFinish, "000001"), await rig.NextRmiHexAsync());
    }

 /// <summary>
 /// The close <see cref="WarpService.WarpAsync"/> sends after a pyramid use. A move to another map sends
 /// <c>000001</c> ahead of the departure 2938, before any other frame of the move, and ends the use, so a logout
 /// after it sends nothing. A move on the same map is an in-place 2311: <c>587101</c> is sent directly before the
 /// 2311 and the use stays armed, so a later logout still closes. A caller that closed ahead of its own 2938 gets
 /// no second close from the warp.
 /// </summary>
    [Fact]
    public async Task AWarpClosesTheWayRetailsMoveDoes()
    {
        var data = await LoadMapsAsync(1100, 1113, 3101);
        await using var rig = await Rig.StartAsync();
        await using var services = WarpServices(data);
        var ct = CancellationToken.None;
        const string Close = "000001", CanceledByMovement = "587101";

        var player = WarpingPlayer(rig, mapId: 1100);
        await IllusionPyramidService.NoteStateRequestAsync(player, ct);
        Assert.True(await WarpService.WarpAsync(rig.Session, player, 1113, 0f, 0f, 0f, services));
        var frames = await rig.FramesUntilMarkerAsync();
        Assert.Equal((ZoneS2COpcodes.AnsIllusionPyramidFinish, Close), frames[0]);
        Assert.Single(frames, f => f.Op == ZoneS2COpcodes.AnsIllusionPyramidFinish);
        Assert.Contains(frames, f => f.Op == ZoneS2COpcodes.OnReceiveMapLoad);
        await IllusionPyramidService.CloseOnDepartureAsync(rig.Session, player, services, NullLogger.Instance, ct);
        Assert.Empty(await rig.FramesUntilMarkerAsync());

        player = WarpingPlayer(rig, mapId: 3101);
        await IllusionPyramidService.NoteStateRequestAsync(player, ct);
        Assert.True(await WarpService.WarpAsync(rig.Session, player, 3101, 0f, 0f, 0f, services));
        frames = await rig.FramesUntilMarkerAsync();
        int mapLoad = frames.FindIndex(f => f.Op == ZoneS2COpcodes.OnReceiveMapLoad);
        Assert.True(mapLoad > 0, "the 2311 has a frame before it");
        Assert.Equal((ZoneS2COpcodes.AnsIllusionPyramidFinish, CanceledByMovement), frames[mapLoad - 1]);
        Assert.Single(frames, f => f.Op == ZoneS2COpcodes.AnsIllusionPyramidFinish);
        await IllusionPyramidService.CloseOnDepartureAsync(rig.Session, player, services, NullLogger.Instance, ct);
        Assert.Equal(new[] { (ZoneS2COpcodes.AnsIllusionPyramidFinish, Close) }, await rig.FramesUntilMarkerAsync());

        player = WarpingPlayer(rig, mapId: 1100);
        await IllusionPyramidService.NoteStateRequestAsync(player, ct);
        await IllusionPyramidService.CloseOnDepartureAsync(rig.Session, player, services, NullLogger.Instance, ct);
        Assert.True(await WarpService.WarpAsync(rig.Session, player, 1113, 0f, 0f, 0f, services));
        frames = await rig.FramesUntilMarkerAsync();
        Assert.Equal((ZoneS2COpcodes.AnsIllusionPyramidFinish, Close), frames[0]);
        Assert.Single(frames, f => f.Op == ZoneS2COpcodes.AnsIllusionPyramidFinish);
    }

 /// <summary>
 /// A finish, a stone start, an advance, then a colosseum round's in-place 2311. The held item is settled
 /// (2329, 2333, then 2909 <c>587101</c>) and the run ends; the client's next 1619 is answered 29007.
 /// </summary>
    [Fact]
    public void ALeaveSettlesAndClosesTheRun()
    {
        var slot = new IllusionPyramidRunStore.Slot { UsedSinceLeave = true };
        IllusionPyramidService.Close(slot); 
        static long T(string time) => (long)TimeSpan.Parse(time).TotalMilliseconds;

        var start = Draw("01107979889303");
        Assert.Equal(0, IllusionPyramidService.Check(slot, start, T("08:39:51.146")));
        IllusionPyramidService.Accept(slot, start, IllusionPyramidCatalog.Find(1, 5)!, T("08:39:51.146"));
        Assert.NotNull(IllusionPyramidService.Fetch(slot));

        var advance = Draw("02137979889303");
        Assert.Equal(0, IllusionPyramidService.Check(slot, advance, T("08:39:53.669")));
        var held = IllusionPyramidCatalog.Find(2, 6)!;
        
        Assert.Equal((60000800u, (byte)5, false), (held.ItemId, held.Count, held.IsMileage));
        IllusionPyramidService.Accept(slot, advance, held, T("08:39:53.669"));
        Assert.Same(held, IllusionPyramidService.Fetch(slot));

        
        Assert.True(IllusionPyramidService.BeginLeave(slot, endsUse: false));
        Assert.Same(held, slot.Run!.Held);
        Assert.True(slot.UsedSinceLeave);
        IllusionPyramidService.Close(slot); 

        
        Assert.False(slot.DrewSinceFinish);
        Assert.Null(slot.Run);

        
        Assert.True(IllusionPyramidService.BeginLeave(slot, endsUse: true));
        Assert.False(IllusionPyramidService.BeginLeave(slot, endsUse: true));
    }

 /// <summary>
 /// A mileage end whose entry cannot be charged is refused, not shown: <c>[code]</c> followed by ten zero bytes,
 /// the form of the no-result 2908. The code is the start check's code for the same missing entry. No captured
 /// frame covers this case, so the 29001 bytes are derived.
 /// </summary>
    [Fact]
    public void AnUnpayableResultIsRefused()
    {
        Assert.Equal(Convert.FromHexString("4f7100000000000000000000"),
            IllusionPyramidService.Build2908Code(IllusionPyramidService.ResultNotExploring));
        Assert.Equal(IllusionPyramidService.ResultNeedPhantomStone,
            IllusionPyramidService.EntryRefusal(IllusionPyramidCatalog.EntryKind.PhantomStone));
        Assert.Equal(IllusionPyramidService.ResultAbnormalInformation,
            IllusionPyramidService.EntryRefusal(IllusionPyramidCatalog.EntryKind.Mystical));
        Assert.Equal(IllusionPyramidService.ResultAbnormalInformation,
            IllusionPyramidService.EntryRefusal(IllusionPyramidCatalog.EntryKind.Bright));
        Assert.Equal(Convert.FromHexString("497100000000000000000000"),
            IllusionPyramidService.Build2908Code(IllusionPyramidService.ResultNeedPhantomStone));
    }

    private static IllusionPyramidService.DrawRequest Draw(string hex)
    {
        Assert.True(IllusionPyramidService.TryParseDraw(Convert.FromHexString(hex), out var req));
        return req;
    }

 /// <summary>A store holding only Map_List rows for <paramref name="mapIds"/>, loaded the way the server loads
 /// data/ (a warp refuses a map it does not know).</summary>
    private static async Task<GameDataStore> LoadMapsAsync(params uint[] mapIds)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ro2-pyramid-warp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "Map_List.json"),
                "[" + string.Join(",", mapIds.Select(id => "{\"Map_Index\":" + id + "}")) + "]");
            var store = new GameDataStore();
            await store.LoadAsync(dir);
            return store;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

 /// <summary>
 /// What <see cref="WarpService.WarpAsync"/> resolves: no NPC spawns, no worn gear, no other player on the map, and
 /// no database, so the key-config and vehicle reads take their storage-error fallbacks.
 /// </summary>
    private static ServiceProvider WarpServices(GameDataStore data)
    {
        var players = new PlayerStateManager(new NoDirtySink());
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        var collection = new ServiceCollection();
        collection.AddSingleton(data);
        collection.AddSingleton<IInventoryRepository>(new NoGearInventory());
        collection.AddSingleton(sp => new PlayerSpawnService(players, broadcast, NullLogger<PlayerSpawnService>.Instance,
            sp.GetRequiredService<IServiceScopeFactory>(), TestEmblems.Create()));
        collection.AddSingleton(new NpcSpawnService(data, new NpcStateManager(data, NullLogger<NpcStateManager>.Instance),
            NullLogger<NpcSpawnService>.Instance));
        collection.AddSingleton(new EffectService(players: null!, broadcast: null!, data: data, npcState: null!,
            loot: null!, scopeFactory: null!, logger: NullLogger<EffectService>.Instance, emblems: TestEmblems.Create()));
        return collection.BuildServiceProvider();
    }

 /// <summary>A fresh in-world PC on <paramref name="mapId"/> (a new PlayerState, so a new pyramid slot).</summary>
    private static PlayerState WarpingPlayer(Rig rig, uint mapId) => new()
    {
        SessionId = rig.Session.SessionId, EntityId = 0x0B2FF411u, CharacterId = 904_311, Name = "Warper",
        Level = 10, MapId = mapId, MapLoaded = true, Hp = 1000, MaxHp = 1000, Mp = 100, MaxMp = 100,
        Character = new Character { Id = 904_311, Name = "Warper", Job = 1, Level = 10 },
    };

    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }

 /// <summary>An empty bag with nothing worn.</summary>
    private sealed class NoGearInventory : IInventoryRepository
    {
        public Task<IReadOnlyList<InventoryItem>> GetByCharacterAsync(uint characterId) =>
            Task.FromResult<IReadOnlyList<InventoryItem>>(Array.Empty<InventoryItem>());
        public Task<IReadOnlyList<InventoryItem>> GetEquipmentAsync(uint characterId) =>
            Task.FromResult<IReadOnlyList<InventoryItem>>(Array.Empty<InventoryItem>());
        public Task<InventoryItem?> GetByIdAsync(ulong id) => Task.FromResult<InventoryItem?>(null);
        public Task AddItemAsync(InventoryItem item) => Task.CompletedTask;
        public Task RemoveItemAsync(ulong itemId) => Task.CompletedTask;
        public Task UpdateItemAsync(InventoryItem item) => Task.CompletedTask;
        public Task<ulong> GetNextIdAsync() => Task.FromResult(1UL);
        public Task<int> RepairSlotsAsync(uint characterId) => Task.FromResult(0);
    }

 /// <summary>A real <see cref="ProudSession"/> on a loopback <see cref="ProudServer"/>, read from the client end.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly ProudServer _server;
        private readonly Socket _client;

        public ProudSession Session { get; }

        private Rig(ProudServer server, Socket client, ProudSession session)
        {
            _server = server;
            _client = client;
            Session = session;
        }

        public static async Task<Rig> StartAsync()
        {
            var server = new ProudServer("test", NullLogger.Instance);
            var connected = new TaskCompletionSource<ProudSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.SessionConnected += s => { connected.TrySetResult(s); return Task.CompletedTask; };
            await server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync(server.LocalEndPoint!);
            var session = await connected.Task.WaitAsync(Generous);
            return new Rig(server, client, session);
        }

 /// <summary>The next internal RMI frame (<c>[13 57][w][len][01][u16 op][body]</c>) as (opcode, body hex);
 /// frames of any other message type are skipped.</summary>
        public async Task<(ushort Op, string Body)> NextRmiHexAsync()
        {
            while (true)
            {
                var head = await ReadExactAsync(3);
                Assert.Equal(0x13, head[0]);
                Assert.Equal(0x57, head[1]);
                var lengthBytes = await ReadExactAsync(head[2]);
                int length = head[2] switch
                {
                    1 => lengthBytes[0],
                    2 => BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes),
                    _ => BinaryPrimitives.ReadInt32LittleEndian(lengthBytes),
                };
                var data = await ReadExactAsync(length);
                if (data.Length >= 3 && data[0] == 0x01)
                    return (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(1)),
                        Convert.ToHexString(data, 3, data.Length - 3).ToLowerInvariant());
            }
        }

 /// <summary>Sends a 2907 <c>000000</c> marker and returns, in order, every RMI frame the client receives
 /// before it.</summary>
        public async Task<List<(ushort Op, string Body)>> FramesUntilMarkerAsync()
        {
            await Session.SendRmiAsync(ZoneS2COpcodes.AnsIllusionPyramidDrawStage, IllusionPyramidService.Build2907(0));
            var frames = new List<(ushort Op, string Body)>();
            while (true)
            {
                var frame = await NextRmiHexAsync();
                if (frame == (ZoneS2COpcodes.AnsIllusionPyramidDrawStage, "000000")) return frames;
                frames.Add(frame);
            }
        }

        private async Task<byte[]> ReadExactAsync(int count)
        {
            var buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = await _client.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None).AsTask().WaitAsync(Generous);
                if (n == 0) throw new EndOfStreamException("connection closed mid-frame");
                read += n;
            }
            return buffer;
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _server.StopAsync();
        }
    }
}
