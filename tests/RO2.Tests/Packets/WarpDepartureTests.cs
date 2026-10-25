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
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Tests.Services;
using Xunit;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// Warp departure (WarpDeparture) and the move that follows it (WarpService.WarpAsync), checked against reference frames.
/// </summary>
/// <remarks>
/// Belongs to the MountService state collection together with RidingServiceTests: a departure drops the ride,
/// and MountService is process-wide and keyed by session id.
/// </remarks>
[Collection(MountStateCollection.Name)]
public class WarpDepartureTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);
    private static readonly Lazy<Task<GameDataStore>> Data = new(LoadDataAsync);

    [Fact]
    public async Task WarpHandlingZeroIsWhatAWarpEnds()
    {
        var data = await Data.Value;
        foreach (var dropped in new uint[] { 60205, 60037, 60043, 40013, 210020 })
            Assert.True(WarpDeparture.IsDroppedOnWarp(data, dropped));
        foreach (var kept in new uint[] { 100, 40000, 40011, 40012, 62005 })
            Assert.False(WarpDeparture.IsDroppedOnWarp(data, kept));
        Assert.False(WarpDeparture.IsDroppedOnWarp(data, 999_999_999)); 
    }

 /// <summary>
 /// Warp departure from a fortress teleporter for a rider with the fortress effects and guild buffs active. Expected
 /// frames: 2938 and the self 2505 first, then the effect removals and re-sends in the reference order, then 2558.
 /// The 2315 sent for the removal of the max-HP effect is the PC's record re-sent after the max HP changed; it is
 /// compared on its msgTag, zero bytes and handle. The 2498 frames are the character's own sheet, so only their
 /// opcode and length are compared. All other bodies must match the reference byte for byte.
 /// </summary>
    [Fact]
    public async Task DepartureIsRetails1615Window()
    {
        var data = await Data.Value;
        await using var rig = await Rig.StartAsync(data, spareSessions: 9);
        var player = rig.AddPlayer(0x0BF86911u);
        
        foreach (var (id, level) in new (uint, byte)[]
                 { (40013, 1), (60043, 1), (40000, 1), (40009, 10), (40010, 10), (40011, 10), (40012, 10) })
            player.ActiveEffects.Add(new ActiveEffect { EffectId = id, EffectLevel = level, TargetHandle = player.EntityId });
        
        rig.Services.GetRequiredService<EffectService>().RefreshPcTraitSheet(player, player.Character!, Array.Empty<InventoryItem>());
        
        
        await RidingService.MountAsync(rig.Session, player, vehicleId: 90230000, skillId: 6003801, rig.Services);
        Assert.Equal(new[] { ZoneS2COpcodes.NfyRideRidingPet, ZoneS2COpcodes.OnChangeActorMoveSpeed, ZoneS2COpcodes.OnAddEffect },
            (await rig.FramesUntilMarkerAsync()).Select(f => f.Op).ToArray());

        await WarpDeparture.DepartAsync(rig.Session, player, rig.Services, NullLogger.Instance, CancellationToken.None);
        var frames = await rig.FramesUntilMarkerAsync();

        const string Sheet = "2498", ViewData = "2315";
        var expected = new (ushort Op, string Body)[]
        {
            (ZoneS2COpcodes.OnPcTrait, Sheet),
            (ZoneS2COpcodes.OnDelEffectSuccess, "01141400c8e3000000004d9c00000000000000000000"),
            (ZoneS2COpcodes.OnDelEffect, "01181800c9e3000000004d9c00001169f80b0000000000000000"),
            (ZoneS2COpcodes.OnPcTrait, Sheet),
            (ZoneS2COpcodes.OnPCViewData, ViewData),
            (ZoneS2COpcodes.OnDelEffectSuccess, "01141400c8e3000000008bea00000000000000000000"),
            (ZoneS2COpcodes.OnDelEffect, "01181800c9e3000000008bea00001169f80b0000000000000000"),
            (ZoneS2COpcodes.OnChangeActorMoveSpeed, "01101000fce3000000001169f80b0000c040"),
            (ZoneS2COpcodes.OnPcTrait, Sheet),
            (ZoneS2COpcodes.OnDelEffectSuccess, "01141400c8e300000000640000000000000000000000"),
            (ZoneS2COpcodes.OnDelEffect, "01181800c9e300000000640000001169f80b0000000000000000"),
            (ZoneS2COpcodes.NfyDropRidingPet, "011414004de4000000001169f80b599c5b00f0cc6005"),
            (ZoneS2COpcodes.AnsSiegeArmsUseEnd, "0100"),
            (ZoneS2COpcodes.OnPC_Out, "010e0e00cfe31169f80b01001169f80b"),
            (ZoneS2COpcodes.OnPcTraitChange, "01191900fde30000000004000000400000000000fa43409c000001"),
            (ZoneS2COpcodes.OnPcTrait, Sheet),
            (ZoneS2COpcodes.OnPcTraitChange, "01191900fde300000000040000004500000000007a44499c000001"),
            (ZoneS2COpcodes.OnPcTrait, Sheet),
            (ZoneS2COpcodes.OnPcTraitChange, "01191900fde300000000040000004e00000000007a444a9c000001"),
            (ZoneS2COpcodes.OnPcTrait, Sheet),
            (ZoneS2COpcodes.OnPcTraitChange, "01191900fde300000000040000001500000000007a444b9c000001"),
            (ZoneS2COpcodes.OnPcTrait, Sheet),
            (ZoneS2COpcodes.OnPcTraitChange, "01191900fde300000000040000001700000000007a444c9c000001"),
            (ZoneS2COpcodes.OnPcTraitChange, "01191900fde300000000040000001900000000007a444c9c000001"),
            (ZoneS2COpcodes.OnPcTrait, Sheet),
        };
        Assert.Equal(expected.Length, frames.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Op, frames[i].Op);
            if (expected[i].Body == Sheet)
                Assert.Equal(TraitPackets.PcTraitBodyLen * 2, frames[i].Body.Length);
            else if (expected[i].Body == ViewData)
                Assert.StartsWith("97e2000000001169f80b", frames[i].Body[(2 + 2 * Convert.FromHexString(frames[i].Body[..2])[0] + 4)..]);
            else
                Assert.Equal(expected[i].Body, frames[i].Body);
        }

        Assert.False(MountService.IsMounted(rig.Session.SessionId));
        lock (player.ActiveEffects)
            Assert.Equal(new uint[] { 40000, 40009, 40010, 40011, 40012 }, player.ActiveEffects.Select(e => e.EffectId).ToArray());
    }

 /// <summary>
 /// With nothing to drop and nothing kept, the departure consists of 2938 and the self 2505 alone.
 /// </summary>
    [Fact]
    public async Task BareDepartureIsTheAckAndTheSelfOut()
    {
        var data = await Data.Value;
        await using var rig = await Rig.StartAsync(data, spareSessions: 9);
        var player = rig.AddPlayer(0x07639D11u);

        await WarpDeparture.DepartAsync(rig.Session, player, rig.Services, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(new[]
        {
            (ZoneS2COpcodes.AnsSiegeArmsUseEnd, "0100"),
            (ZoneS2COpcodes.OnPC_Out, "010e0e00cfe3119d63070100119d6307"),
        }, await rig.FramesUntilMarkerAsync());
    }

 /// <summary>
 /// The move after the departure: no 2506 precedes the 2311, and the collection trio follows the 2522. With no
 /// collection store the trio carries the forms of a character that registered nothing and has no active pet
 /// (2918 result 30001).
 /// </summary>
    [Fact]
    public async Task WarpSendsNoNpcsAndEndsWithTheCollectionTrio()
    {
        var data = await Data.Value;
        await using var rig = await Rig.StartAsync(data, spareSessions: 9);
        var player = rig.AddPlayer(0x07639D11u);

        Assert.True(await WarpService.WarpAsync(rig.Session, player, 1101, 0f, 0f, 0f, rig.Services));
        var ops = (await rig.FramesUntilMarkerAsync()).Select(f => f.Op).ToList();

        int mapLoad = ops.IndexOf(ZoneS2COpcodes.OnReceiveMapLoad);
        Assert.True(mapLoad >= 0);
        Assert.DoesNotContain(ZoneS2COpcodes.OnNPC_In, ops.Take(mapLoad));
        Assert.Equal(new ushort[]
        {
            ZoneS2COpcodes.OnQuickSlotSaveData, ZoneS2COpcodes.OnVehicleQuickSlot, ZoneS2COpcodes.OnAllVehicleList,
            ZoneS2COpcodes.NfyMCollection, ZoneS2COpcodes.NfyPCollection, ZoneS2COpcodes.AnsPCollectionSetActivePet,
        }, ops.Skip(mapLoad + 1).ToArray());
        Assert.Equal(1101u, player.MapId);
    }

 /// <summary>
 /// A whole 1371 request through WarpHandler, from a reference window: a player inside an agit on map 5101 with the
 /// map-move invincibility effect (60205) sends 1371 for the gate NPC 31000000 (Function_Type_1 12, Function_ID_1 350)
 /// with option 12. The reference answer, in order: 2498 2493 2494 (60205), 2938 [01 00], the self 2505,
 /// 2558 2743 2595 2498 2669, then 2311 to Position_Info 350 (map 1100 at 725, 796, 55.5). The request is the
 /// reference body with the gate handle allocated by this server placed at body+10. The 2498s are compared on opcode
 /// and length, the 2595 on the 64 bytes the reference keeps, the 2669 on length (clock), and the 2311 up to byte 44
 /// (see WorldEntryService.BuildMapLoad2311). All other bodies must match byte for byte. The 2311 is followed by
 /// 2472 2523 2522 and the collection trio.
 /// </summary>
    [Fact]
    public async Task GateRequestIsRetails1371Window()
    {
        var data = await Data.Value;
        await using var rig = await Rig.StartAsync(data, spareSessions: 9);
        var player = rig.AddPlayer(0x0B9DB511u);
        player.MapId = 5101;
        player.ActiveEffects.Add(new ActiveEffect { EffectId = 60205, EffectLevel = 1, TargetHandle = player.EntityId });
        rig.Npcs.InitializeMap(5101);
        var gate = rig.Npcs.GetAliveNpcs(5101).First(n => n.NpcId == 31000000);

        var request = Convert.FromHexString("01101000e6e211b59d0b13b431000c000000");
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(10), gate.EntityOid);
        await rig.NewWarpHandler().HandleAsync(rig.Session, request);
        var frames = await rig.FramesUntilMarkerAsync();

        const string Sheet = "2498";
        var expected = new (ushort Op, string Body)[]
        {
            (ZoneS2COpcodes.OnPcTrait, Sheet),
            (ZoneS2COpcodes.OnDelEffectSuccess, "01141400c8e3000000002deb00000000000000000000"),
            (ZoneS2COpcodes.OnDelEffect, "01181800c9e3000000002deb000011b59d0b0000000000000000"),
            (ZoneS2COpcodes.AnsSiegeArmsUseEnd, "0100"),
            (ZoneS2COpcodes.OnPC_Out, "010e0e00cfe311b59d0b010011b59d0b"),
            (ZoneS2COpcodes.Notify_ZSCLN_DB_ACCOUNT_CONFIG, "0700000011b59d0b000000"),
            (ZoneS2COpcodes.OnSendDependentDungeonInfo, "020000000000"),
            (ZoneS2COpcodes.OnPcInitTrait,
                "028c008c0096e3000000000000000000000100010001000100010000000000000000000000000000000000000000000000000000000000000000000000000000"),
            (ZoneS2COpcodes.OnPcTrait, Sheet),
            (ZoneS2COpcodes.NfyServerTimeToLoginPC, "83907e61167df302"), 
            (ZoneS2COpcodes.OnReceiveMapLoad,
                "012b2b0094e211b59d0b4c040000000000000000000000000000004035440000474400005e42000011b59d0b"),
        };
        Assert.True(frames.Count > expected.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Op, frames[i].Op);
            if (expected[i].Body == Sheet)
                Assert.Equal(TraitPackets.PcTraitBodyLen * 2, frames[i].Body.Length);
            else if (expected[i].Op == ZoneS2COpcodes.NfyServerTimeToLoginPC)
                Assert.Equal(expected[i].Body.Length, frames[i].Body.Length);
            else if (expected[i].Op == ZoneS2COpcodes.OnPcInitTrait)
                Assert.Equal(expected[i].Body, frames[i].Body[..expected[i].Body.Length]);
            else if (expected[i].Op == ZoneS2COpcodes.OnReceiveMapLoad)
                Assert.Equal(expected[i].Body, frames[i].Body[..expected[i].Body.Length]);
            else
                Assert.Equal(expected[i].Body, frames[i].Body);
        }
        var after = frames.Skip(expected.Length).Select(f => f.Op).ToArray();
        Assert.Equal(new[] { ZoneS2COpcodes.OnQuickSlotSaveData, ZoneS2COpcodes.OnVehicleQuickSlot, ZoneS2COpcodes.OnAllVehicleList },
            after.Take(3).ToArray());
        Assert.Equal(new[] { ZoneS2COpcodes.NfyMCollection, ZoneS2COpcodes.NfyPCollection, ZoneS2COpcodes.AnsPCollectionSetActivePet }
            .Order().ToArray(), after.Skip(3).Order().ToArray());

        Assert.Equal(1100u, player.MapId);
        Assert.Equal((725f, 796f, 55.5f), (player.X, player.Y, player.Z));
        lock (player.ActiveEffects)
            Assert.Empty(player.ActiveEffects);
    }

 /// <summary>
 /// A 1615 request at a teleporter whose destinations this server does not serve is answered with 2905 [03 5F] alone,
 /// and the player stays in place. NPC 30940003 (group 6) slot 1 leads to the south-fortress agit (Position_Info 349).
 /// The reference gate on these teleporters depends on the destination and the requester. This server keeps no siege
 /// ownership (see MultiTeleportNpcHandler), so every player receives the refusal.
 /// </summary>
    [Fact]
    public async Task UnservedTeleporterIsRefusedWithTheVictoriousGuildCode()
    {
        var data = await Data.Value;
        await using var rig = await Rig.StartAsync(data, spareSessions: 9);
        var player = rig.AddPlayer(0x0B9DB511u);
        rig.Npcs.InitializeMap(1100);
        var teleporter = rig.Npcs.GetAliveNpcs(1100).First(n => n.NpcId == 30940003);

        var request = new byte[9];
        BinaryPrimitives.WriteUInt32LittleEndian(request, 5);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), teleporter.EntityOid);
        request[8] = 1;
        await rig.NewMultiTeleportNpcHandler().HandleAsync(rig.Session, request);

        Assert.Equal(new[] { (ZoneS2COpcodes.AnsMultiTeleportNpc, "035f") }, await rig.FramesUntilMarkerAsync());
        Assert.Equal(1100u, player.MapId);
    }

 /// <summary>
 /// The (NPC, slot) to Position_Info rows served by MultiTeleportNpcHandler, each matching the destination observed in
 /// the reference traffic, and the agit groups it refuses.
 /// </summary>
    [Theory]
    [InlineData(17260000u, 1, 274u)]
    [InlineData(17260000u, 4, 327u)]
    [InlineData(28950000u, 1, 275u)]
    [InlineData(31120000u, 1, 370u)]
    [InlineData(31130000u, 1, 378u)]
    [InlineData(31030000u, 1, 381u)]
    [InlineData(31030001u, 4, 385u)]
    [InlineData(30940001u, 1, 0u)]
    [InlineData(31520000u, 2, 0u)]
    public async Task TeleporterDestinationIsTheArchivesRow(uint npcId, int slot, uint positionId)
    {
        var data = await Data.Value;
        bool served = MultiTeleportNpcHandler.TryResolveDestination(data, npcId, (byte)slot, out var destination);
        Assert.Equal(positionId != 0, served);
        if (served)
            Assert.Equal(positionId, destination.Position_ID);
    }

    private static async Task<GameDataStore> LoadDataAsync()
    {
        var store = new GameDataStore();
        await store.LoadAsync(RequireDataDirectory());
        return store;
    }

 /// <summary>Walks up from the test output path to the repository's data directory.</summary>
    private static string RequireDataDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "Effect_Table.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/Effect_Table.json");
    }

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

 /// <summary>
 /// A real <see cref="ProudSession"/> on a loopback <see cref="ProudServer"/>, read from the client end, with the
 /// singletons the departure, the warp and the two request handlers resolve: GameDataStore, BroadcastService,
 /// EffectService, PlayerSpawnService, an NpcStateManager (no map tracked until a test initializes one) and an empty
 /// inventory, with no database (the key-config, vehicle and collection reads take their storage-error fallbacks).
 /// <c>spareSessions</c> connections are opened first so that the session id differs from those of other
 /// test rigs.
 /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly ProudServer _server;
        private readonly Socket _client;
        private readonly Socket[] _spare;
        private readonly ServiceProvider _services;
        private readonly BroadcastService _broadcast;

        public ProudSession Session { get; }
        public PlayerStateManager Players { get; }
        public NpcStateManager Npcs { get; }
        public IServiceProvider Services => _services;

        private Rig(ProudServer server, Socket client, Socket[] spare, ProudSession session, GameDataStore data)
        {
            _server = server;
            _client = client;
            _spare = spare;
            Session = session;
            Players = new PlayerStateManager(new NoDirtySink());
            Npcs = new NpcStateManager(data, NullLogger<NpcStateManager>.Instance);
            var broadcast = new BroadcastService(Players, NullLogger<BroadcastService>.Instance);
            broadcast.SetServer(server);
            _broadcast = broadcast;
            var players = Players;
            var collection = new ServiceCollection();
            collection.AddSingleton(data);
            collection.AddSingleton(broadcast);
            collection.AddSingleton(Npcs);
            collection.AddSingleton<IInventoryRepository>(new NoGearInventory());
            collection.AddSingleton(sp => new EffectService(players, broadcast, data, npcState: null!, loot: null!,
                sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<EffectService>.Instance, emblems: TestEmblems.Create()));
            collection.AddSingleton(sp => new PlayerSpawnService(players, broadcast, NullLogger<PlayerSpawnService>.Instance,
                sp.GetRequiredService<IServiceScopeFactory>(), TestEmblems.Create()));
            _services = collection.BuildServiceProvider();
        }

        public static async Task<Rig> StartAsync(GameDataStore data, int spareSessions)
        {
            var server = new ProudServer("test", NullLogger.Instance);
            var sessions = new List<ProudSession>();
            var connected = new SemaphoreSlim(0);
            server.SessionConnected += s =>
            {
                lock (sessions) sessions.Add(s);
                connected.Release();
                return Task.CompletedTask;
            };
            await server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
            var spare = new Socket[spareSessions];
            for (int i = 0; i < spareSessions; i++)
            {
                spare[i] = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await spare[i].ConnectAsync(server.LocalEndPoint!);
                Assert.True(await connected.WaitAsync(Generous));
            }
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync(server.LocalEndPoint!);
            Assert.True(await connected.WaitAsync(Generous));
            ProudSession session;
            lock (sessions) session = sessions[^1];
            Assert.Equal((uint)spareSessions + 1, session.SessionId);
            MountService.Dismount(session.SessionId);
            return new Rig(server, client, spare, session, data);
        }

 /// <summary>An in-world PC on map 1100, alone there, registered for the broadcasts.</summary>
        public PlayerState AddPlayer(uint entityId)
        {
            var player = new PlayerState
            {
                SessionId = Session.SessionId, EntityId = entityId, CharacterId = 915_201, Name = "Traveller",
                Level = 60, MapId = 1100, MapLoaded = true, Hp = 5000, MaxHp = 5000, Mp = 500, MaxMp = 500,
                Character = new Character { Id = 915_201, Name = "Traveller", Job = 1, Level = 60 },
            };
            Players.AddPlayer(Session.SessionId, player);
            
            
            _services.GetRequiredService<EffectService>().RefreshPcTraitSheet(player, player.Character!, Array.Empty<InventoryItem>());
            return player;
        }

 /// <summary>The 1371 handler over this rig's services, players and broadcast.</summary>
        public WarpHandler NewWarpHandler() => new(_services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<WarpHandler>.Instance, Players, _broadcast);

 /// <summary>The 1615 handler over this rig's services, players and broadcast.</summary>
        public MultiTeleportNpcHandler NewMultiTeleportNpcHandler() => new(_services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MultiTeleportNpcHandler>.Instance, Players, _broadcast);

        private async Task<(ushort Op, string Body)> NextRmiHexAsync()
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

 /// <summary>Sends a marker frame (opcode 0, body <c>7e57</c>) and returns, in order, every RMI frame the client
 /// receives before it.</summary>
        public async Task<List<(ushort Op, string Body)>> FramesUntilMarkerAsync()
        {
            await Session.SendInternalRmiAsync(0, new byte[] { 0x7E, 0x57 });
            var frames = new List<(ushort Op, string Body)>();
            while (true)
            {
                var frame = await NextRmiHexAsync();
                if (frame == (0, "7e57")) return frames;
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
            MountService.Dismount(Session.SessionId);
            _client.Dispose();
            foreach (var s in _spare) s.Dispose();
            await _services.DisposeAsync();
            await _server.StopAsync();
        }
    }
}
