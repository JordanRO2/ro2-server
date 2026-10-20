using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Services;

/// <summary>
/// Riding pet behavior: the mounted speed from RidingInfo and the 1510 answers (mount, dismount, switch) through
/// RideRidingPetHandler and RidingService onto the wire, compared with reference frames.
/// </summary>
/// <remarks>
/// Serialized with <see cref="RO2.Tests.Packets.WarpDepartureTests"/>: both hold MountService entries, which are
/// process-wide and keyed by session id. The rigs here take session ids that no other test uses, and
/// IllusionPyramidServiceTests warps on id 1 through WarpService.WarpAsync, which drops that id's ride.
/// </remarks>
[Collection(MountStateCollection.Name)]
public class RidingServiceTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);
    private static readonly Lazy<Task<GameDataStore>> Data = new(LoadDataAsync);

    [Fact]
    public async Task RideRowIsTheLastNationEnabledRow()
    {
        var data = await Data.Value;

        
        Assert.Equal(1.6f, RidingService.RideRow(data, 90140000)!.Speed);
        Assert.Equal(1.4f, RidingService.RideRow(data, 90110000)!.Speed);
        
        Assert.Equal(1.7f, RidingService.RideRow(data, 90200000)!.Speed);
        Assert.Equal(1.7f, RidingService.RideRow(data, 90210000)!.Speed);
        
        Assert.Null(RidingService.RideRow(data, 90100001));
        Assert.Null(RidingService.MountedSpeed(data, 90100001, Array.Empty<ActiveEffect>()));
    }

 /// <summary>Mounted speed of vehicle 90020000 (Speed 1.4) at a multiplier of 1.0 is 7.0.</summary>
    [Fact]
    public async Task MountedSpeedWithoutARidingEffectIsRetailsPlainMount()
    {
        var data = await Data.Value;
        var speed = RidingService.MountedSpeed(data, 90020000, Array.Empty<ActiveEffect>());
        Assert.NotNull(speed);
        Assert.Equal(Convert.FromHexString("01101000fce300000000115b9f0b0000e040"),
            TraitPackets.BuildActorMoveSpeed2497(0x0B9F5B11u, speed!.Value));
    }

 /// <summary>
 /// With guild skill effect 40000 (Trait_Kind 4, Task_Type 64, +500) the factor is 1.05. The single-precision
 /// multiplication order matches the reference: vehicle 90020000 gives 7.35, and vehicle 90240000 (Speed 1.7)
 /// gives 8.925, where multiplying 5 by 1.7 first would round differently.
 /// </summary>
    [Fact]
    public async Task GuildRidingSpeedEffectGivesRetailsBoostedMount()
    {
        var data = await Data.Value;
        var guildSprint = new[] { new ActiveEffect { EffectId = 40000 } };

        Assert.Equal(Convert.FromHexString("01101000fce3000000001104a20b3233eb40"),
            TraitPackets.BuildActorMoveSpeed2497(0x0BA20411u, RidingService.MountedSpeed(data, 90020000, guildSprint)!.Value));
        Assert.Equal(Convert.FromHexString("01101000fce30000000011ece707cdcc0e41"),
            TraitPackets.BuildActorMoveSpeed2497(0x07E7EC11u, RidingService.MountedSpeed(data, 90240000, guildSprint)!.Value));
    }

    [Fact]
    public async Task OnlyRidingSkillsKeepTheRide()
    {
        var data = await Data.Value;
        Assert.True(RidingService.KeepsRide(data, 6200501));  
        Assert.True(RidingService.KeepsRide(data, 6000301));  
        Assert.False(RidingService.KeepsRide(data, 410801));  
    }

 /// <summary>Mount: the 1510 is answered with exactly these four frames, in this order.</summary>
    [Fact]
    public async Task MountIsRetailsFourFrames()
    {
        var data = await Data.Value;
        await using var rig = await Rig.StartAsync(data, spareSessions: 5);
        var player = rig.AddPlayer(0x0B9F5B11u);

        await rig.Handler.HandleAsync(rig.Session, Convert.FromHexString("a0985d05ad8e5b00000000000000000068de45b9167df302"));

        Assert.Equal(new[]
        {
            (ZoneS2COpcodes.NfyRideRidingPet, "011414004ce400000000ad8e5b00115b9f0ba0985d05"),
            (ZoneS2COpcodes.OnChangeActorMoveSpeed, "01101000fce300000000115b9f0b0000e040"),
            (ZoneS2COpcodes.OnAddEffect, "01242400c7e300000000115b9f0b640000000100000000000000000001ad8e5b0000115b9f0b"),
            (ZoneS2COpcodes.AnsRideRidingPet, "0000a0985d05ad8e5b000000000000000000"),
        }, await rig.FramesUntilMarkerAsync());
        Assert.True(MountService.IsMounted(rig.Session.SessionId));
        lock (player.ActiveEffects)
            Assert.Contains(player.ActiveEffects, e => e.EffectId == RidingService.RidingEffectId);
    }

 /// <summary>
 /// Dismount of vehicle 90020000: 2497, 2498, 2493(100), 2494(100), 2712, then 2719 21801. The 2497 carries the
 /// per-PC unmounted speed, which this server replaces with its modal 6.0 (TraitPackets.PcModalWorldEntrySpeed), so
 /// only its speed bytes differ from the reference. The 2498 is the character's own sheet, so only its opcode and
 /// length are compared.
 /// </summary>
    [Fact]
    public async Task RepeatingTheVehicleIsRetailsDismount()
    {
        var data = await Data.Value;
        await using var rig = await Rig.StartAsync(data, spareSessions: 5);
        var player = rig.AddPlayer(0x0F199111u);
        await rig.Handler.HandleAsync(rig.Session, Convert.FromHexString("a0985d05ad8e5b00000000000000000068de45b9167df302"));
        Assert.Equal(4, (await rig.FramesUntilMarkerAsync()).Count);

        await rig.Handler.HandleAsync(rig.Session, Convert.FromHexString("a0985d05ad8e5b00000000000000000068e34af2847df302"));
        var frames = await rig.FramesUntilMarkerAsync();

        Assert.Equal(6, frames.Count);
        Assert.Equal(ZoneS2COpcodes.OnChangeActorMoveSpeed, frames[0].Op);
        Assert.StartsWith("01101000fce3000000001191190f", frames[0].Body); 
        Assert.Equal(Convert.ToHexString(TraitPackets.BuildActorMoveSpeed2497(0x0F199111u, TraitPackets.PcModalWorldEntrySpeed)).ToLowerInvariant(),
            frames[0].Body);
        Assert.Equal(ZoneS2COpcodes.OnPcTrait, frames[1].Op);
        Assert.Equal(TraitPackets.PcTraitBodyLen * 2, frames[1].Body.Length);
        Assert.Equal(new[]
        {
            (ZoneS2COpcodes.OnDelEffectSuccess, "01141400c8e300000000640000000000000000000000"),
            (ZoneS2COpcodes.OnDelEffect, "01181800c9e300000000640000001191190f0000000000000000"),
            (ZoneS2COpcodes.NfyDropRidingPet, "011414004de4000000001191190fad8e5b00a0985d05"),
            (ZoneS2COpcodes.AnsRideRidingPet, "2955a0985d05ad8e5b000000000000000000"),
        }, frames.Skip(2).ToArray());
        Assert.False(MountService.IsMounted(rig.Session.SessionId));
        lock (player.ActiveEffects)
            Assert.DoesNotContain(player.ActiveEffects, e => e.EffectId == RidingService.RidingEffectId);
    }

 /// <summary>
 /// Switch from vehicle 90190000 (skill 6002701) to 90150000 (skill 6002301): the old vehicle is dropped (its 2712
 /// names 6002701 and 90190000), the 2719 answer echoes the request with code 17, and nothing is mounted.
 /// </summary>
    [Fact]
    public async Task AnotherVehicleWhileRidingIsRetailsSwitchDrop()
    {
        var data = await Data.Value;
        await using var rig = await Rig.StartAsync(data, spareSessions: 5);
        rig.AddPlayer(0x02105411u);
        
        await rig.Handler.HandleAsync(rig.Session, Convert.FromHexString("b03060050d985b000000000000000000" + "0000000000000000"));
        Assert.Equal(4, (await rig.FramesUntilMarkerAsync()).Count);

        await rig.Handler.HandleAsync(rig.Session, Convert.FromHexString("70945f057d965b00000000000000000039d8d9ed087ef302"));
        var frames = await rig.FramesUntilMarkerAsync();

        Assert.Equal(new ushort[]
        {
            ZoneS2COpcodes.OnChangeActorMoveSpeed, ZoneS2COpcodes.OnPcTrait, ZoneS2COpcodes.OnDelEffectSuccess,
            ZoneS2COpcodes.OnDelEffect, ZoneS2COpcodes.NfyDropRidingPet, ZoneS2COpcodes.AnsRideRidingPet,
        }, frames.Select(f => f.Op).ToArray());
        Assert.Equal("01141400c8e300000000640000000000000000000000", frames[2].Body);
        Assert.Equal("01181800c9e30000000064000000115410020000000000000000", frames[3].Body);
        Assert.Equal("011414004de400000000115410020d985b00b0306005", frames[4].Body);
        Assert.Equal("110070945f057d965b000000000000000000", frames[5].Body);
        Assert.False(MountService.IsMounted(rig.Session.SessionId));
    }

 /// <summary>
 /// A 1418 keeps the connection, and with it the session id that MountService is keyed by and the actor handle.
 /// The next character's 1417 only swaps the PlayerState, which the test does by hand (RemovePlayer, then a new
 /// PlayerState) with the ride left in MountService. That ride must not answer the next character's first 1510 as
 /// a dismount: it gets the mount's four frames, the same bytes as a first mount (see
 /// <see cref="MountIsRetailsFourFrames"/>). The same holds when the player picks the same character again, which
 /// also creates a new PlayerState.
 /// </summary>
    [Fact]
    public async Task TheNextCharacterOfTheConnectionMountsInsteadOfDismounting()
    {
        var data = await Data.Value;
        await using var rig = await Rig.StartAsync(data, spareSessions: 5);
        rig.AddPlayer(0x0B9F5B11u);
        await rig.Handler.HandleAsync(rig.Session, Convert.FromHexString("a0985d05ad8e5b00000000000000000068de45b9167df302"));
        Assert.Equal(4, (await rig.FramesUntilMarkerAsync()).Count);

        Assert.True(rig.Players.RemovePlayer(rig.Session.SessionId, out _));
        var next = rig.AddPlayer(0x0B9F5B11u);
        await rig.Handler.HandleAsync(rig.Session, Convert.FromHexString("a0985d05ad8e5b00000000000000000068de45b9167df302"));

        Assert.Equal(new[]
        {
            (ZoneS2COpcodes.NfyRideRidingPet, "011414004ce400000000ad8e5b00115b9f0ba0985d05"),
            (ZoneS2COpcodes.OnChangeActorMoveSpeed, "01101000fce300000000115b9f0b0000e040"),
            (ZoneS2COpcodes.OnAddEffect, "01242400c7e300000000115b9f0b640000000100000000000000000001ad8e5b0000115b9f0b"),
            (ZoneS2COpcodes.AnsRideRidingPet, "0000a0985d05ad8e5b000000000000000000"),
        }, await rig.FramesUntilMarkerAsync());
        Assert.True(MountService.IsMounted(rig.Session.SessionId));
        lock (next.ActiveEffects)
            Assert.Contains(next.ActiveEffects, e => e.EffectId == RidingService.RidingEffectId);
    }

 /// <summary>
 /// The cast handlers and the departure paths drop the ride through DropAsync. For the next character on the same
 /// connection (see <see cref="TheNextCharacterOfTheConnectionMountsInsteadOfDismounting"/>), the previous
 /// character's ride is not its own, so its first cast sends no 2497, 2494 or 2712 for a vehicle it never rode. The
 /// entry is cleared all the same, without resolving any service.
 /// </summary>
    [Fact]
    public async Task ARideAnEarlierCharacterLeftIsClearedWithoutAFrame()
    {
        var data = await Data.Value;
        await using var rig = await Rig.StartAsync(data, spareSessions: 5);
        rig.AddPlayer(0x0B9F5B11u);
        await rig.Handler.HandleAsync(rig.Session, Convert.FromHexString("a0985d05ad8e5b00000000000000000068de45b9167df302"));
        Assert.Equal(4, (await rig.FramesUntilMarkerAsync()).Count);

        Assert.True(rig.Players.RemovePlayer(rig.Session.SessionId, out _));
        var next = rig.AddPlayer(0x0B9F5B11u);

        Assert.False(await RidingService.DropAsync(rig.Session, next, new ServiceCollection().BuildServiceProvider()));
        Assert.Empty(await rig.FramesUntilMarkerAsync());
        Assert.False(MountService.IsMounted(rig.Session.SessionId));
    }

 /// <summary>
 /// Dropping the ride of a player who is not riding sends nothing and resolves no service.
 /// WarpService.WarpAsync and the cast handlers call it on every move and cast.
 /// </summary>
    [Fact]
    public async Task DropWhenNotRidingSendsNothing()
    {
        var data = await Data.Value;
        await using var rig = await Rig.StartAsync(data, spareSessions: 5);
        var player = rig.AddPlayer(0x0B0B0011u);

        Assert.False(await RidingService.DropAsync(rig.Session, player, new ServiceCollection().BuildServiceProvider()));
        Assert.Empty(await rig.FramesUntilMarkerAsync());
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
            if (File.Exists(Path.Combine(candidate, "RidingInfo.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/RidingInfo.json");
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
 /// handler and the singletons it resolves (GameDataStore, BroadcastService, EffectService and an empty inventory).
 /// <c>spareSessions</c> connections are opened first, so the session id is not one another test's rig
 /// uses (see the class remarks).
 /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly ProudServer _server;
        private readonly Socket _client;
        private readonly Socket[] _spare;
        private readonly ServiceProvider _services;

        public ProudSession Session { get; }
        public PlayerStateManager Players { get; }
        public RideRidingPetHandler Handler { get; }

        private Rig(ProudServer server, Socket client, Socket[] spare, ProudSession session, GameDataStore data)
        {
            _server = server;
            _client = client;
            _spare = spare;
            Session = session;
            Players = new PlayerStateManager(new NoDirtySink());
            var broadcast = new BroadcastService(Players, NullLogger<BroadcastService>.Instance);
            broadcast.SetServer(server);
            var collection = new ServiceCollection();
            collection.AddSingleton(data);
            collection.AddSingleton(broadcast);
            collection.AddSingleton<IInventoryRepository>(new NoGearInventory());
            var players = Players;
            collection.AddSingleton(sp => new EffectService(players, broadcast, data, npcState: null!, loot: null!,
                sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<EffectService>.Instance, emblems: TestEmblems.Create()));
            _services = collection.BuildServiceProvider();
            Handler = new RideRidingPetHandler(_services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<RideRidingPetHandler>.Instance, Players, broadcast);
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

 /// <summary>An in-world PC on map 1100, alone there, registered for the handler and the broadcasts.</summary>
        public PlayerState AddPlayer(uint entityId)
        {
            var player = new PlayerState
            {
                SessionId = Session.SessionId, EntityId = entityId, CharacterId = 915_101, Name = "Rider",
                Level = 30, MapId = 1100, MapLoaded = true, Hp = 1000, MaxHp = 1000, Mp = 100, MaxMp = 100,
                Character = new Character { Id = 915_101, Name = "Rider", Job = 1, Level = 30 },
            };
            Players.AddPlayer(Session.SessionId, player);
            
            
            _services.GetRequiredService<EffectService>().RefreshPcTraitSheet(player, player.Character!, Array.Empty<InventoryItem>());
            return player;
        }

 /// <summary>The next internal RMI frame (<c>[13 57][w][len][01][u16 op][body]</c>) as (opcode, body hex);
 /// frames of any other message type are skipped.</summary>
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

/// <summary>The test classes that hold MountService entries; see <see cref="RidingServiceTests"/>.</summary>
[CollectionDefinition(Name)]
public sealed class MountStateCollection
{
    public const string Name = "MountService state";
}
