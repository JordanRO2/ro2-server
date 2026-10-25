using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData.Tables;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Tests.Services;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Net;
using System.Text.Json;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// Tests the /escape departure (1677, WarpEntryBurst). Runs in the MountService state collection with
/// RidingServiceTests and WarpDepartureTests, since a departure drops the ride and MountService is
/// process-wide and keyed by session id.
/// </summary>
[Collection(MountStateCollection.Name)]
public class EscapeDepartureReauditTests
{
    private static readonly Lazy<Task<GameDataStore>> Data = new(LoadDataAsync);

    
 /// <summary>
 /// A cross-map /escape with a Warp_Handling-0 effect and a kept effect active. The expected order is: the
 /// teardown 2498, 2493 and 2494 before the 2938, then 2938 [01 00], the self 2505, the kept effect's 2499
 /// line(s) and one 2498, then the 2558 burst. The 2315 and 2497 per visible removal are not sent (see
 /// WarpEntryBurst).
 /// </summary>
    [Fact]
    public async Task EscapeDeparture_DropsWarpEffectsThenSendsKeptRuns()
    {
        var data = await Data.Value;
        await using var rig = await Rig.StartAsync(data, spareSessions: 11);
        var player = rig.AddPlayer(0x0B9DB511u);
        player.ActiveEffects.Add(new ActiveEffect { EffectId = 60205, EffectLevel = 1, TargetHandle = player.EntityId });
        player.ActiveEffects.Add(new ActiveEffect { EffectId = 40000, EffectLevel = 1, TargetHandle = player.EntityId });

        await WarpEntryBurst.SendAsync(rig.Session, player, Loaded(player), 1101, 0f, 0f, 0f, rig.Services,
            NullLogger.Instance, CancellationToken.None);
        var frames = await rig.FramesUntilMarkerAsync();

        const string Sheet = "2498";
        var expected = new (ushort Op, string Body)[]
        {
            (ZoneS2COpcodes.OnPcTrait, Sheet),
            (ZoneS2COpcodes.OnDelEffectSuccess, "01141400c8e3000000002deb00000000000000000000"),
            (ZoneS2COpcodes.OnDelEffect, "01181800c9e3000000002deb000011b59d0b0000000000000000"),
            (ZoneS2COpcodes.AnsSiegeArmsUseEnd, "0100"),
            (ZoneS2COpcodes.OnPC_Out, "010e0e00cfe311b59d0b010011b59d0b"),
            (ZoneS2COpcodes.OnPcTraitChange, "01191900fde30000000004000000400000000000fa43409c000001"),
            (ZoneS2COpcodes.OnPcTrait, Sheet),
            (ZoneS2COpcodes.Notify_ZSCLN_DB_ACCOUNT_CONFIG, "0700000011b59d0b000000"),
        };
        Assert.True(frames.Count > expected.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Op, frames[i].Op);
            if (expected[i].Body == Sheet)
                Assert.Equal(TraitPackets.PcTraitBodyLen * 2, frames[i].Body.Length);
            else
                Assert.Equal(expected[i].Body, frames[i].Body);
        }
        Assert.Contains(frames, f => f.Op == ZoneS2COpcodes.OnReceiveMapLoad);

        Assert.Equal(1101u, player.MapId);
        lock (player.ActiveEffects)
            Assert.Equal(new uint[] { 40000 }, player.ActiveEffects.Select(e => e.EffectId).ToArray());
    }

 /// <summary>A departure with no effect active: 2938, self 2505, then the 2558 burst.</summary>
    [Fact]
    public async Task EscapeDeparture_WithNoEffectsGoesStraightToTheBurst()
    {
        var data = await Data.Value;
        await using var rig = await Rig.StartAsync(data, spareSessions: 12);
        var player = rig.AddPlayer(0x0B9DB511u);

        await WarpEntryBurst.SendAsync(rig.Session, player, Loaded(player), 1101, 0f, 0f, 0f, rig.Services,
            NullLogger.Instance, CancellationToken.None);
        var ops = (await rig.FramesUntilMarkerAsync()).Select(f => f.Op).ToArray();

        Assert.Equal(new[]
        {
            ZoneS2COpcodes.AnsSiegeArmsUseEnd, ZoneS2COpcodes.OnPC_Out, ZoneS2COpcodes.Notify_ZSCLN_DB_ACCOUNT_CONFIG,
        }, ops.Take(3).ToArray());
    }

    private static WarpEntryBurst.Loaded Loaded(PlayerState player) => new(player.Character!,
        Array.Empty<CharacterQuickSlot>(), new CollectionDocument { Id = player.CharacterId }, keyConfig: null,
        vehicles: null, Array.Empty<InventoryItem>(), Array.Empty<InventoryItem>());

    private static async Task<GameDataStore> LoadDataAsync()
    {
        var store = new GameDataStore();
        await store.LoadAsync(RequireDataDirectory());
        return store;
    }

 /// <summary>
 /// A real <see cref="ProudSession"/> on a loopback <see cref="ProudServer"/>, read from the client end, with
 /// the singletons the departure and burst resolve (GameDataStore, BroadcastService, EffectService,
 /// PlayerSpawnService, NpcStateManager and an empty inventory, no database). <c>spareSessions</c>
 /// connections are opened first so the session id differs from other tests' rigs.
 /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly ProudServer _server;
        private readonly Socket _client;
        private readonly Socket[] _spare;
        private readonly ServiceProvider _services;

        public ProudSession Session { get; }
        public PlayerStateManager Players { get; }
        public IServiceProvider Services => _services;

        private Rig(ProudServer server, Socket client, Socket[] spare, ProudSession session, GameDataStore data)
        {
            _server = server;
            _client = client;
            _spare = spare;
            Session = session;
            Players = new PlayerStateManager(new NoDirtySink());
            var npcs = new NpcStateManager(data, NullLogger<NpcStateManager>.Instance);
            var broadcast = new BroadcastService(Players, NullLogger<BroadcastService>.Instance);
            broadcast.SetServer(server);
            var players = Players;
            var collection = new ServiceCollection();
            collection.AddSingleton(data);
            collection.AddSingleton(broadcast);
            collection.AddSingleton(npcs);
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

 /// <summary>An in-world character on map 1100, alone there and registered for broadcasts.</summary>
        public PlayerState AddPlayer(uint entityId)
        {
            var player = new PlayerState
            {
                SessionId = Session.SessionId, EntityId = entityId, CharacterId = 915_401, Name = "Escaper",
                Level = 60, MapId = 1100, MapLoaded = true, Hp = 5000, MaxHp = 5000, Mp = 500, MaxMp = 500,
                Character = new Character { Id = 915_401, Name = "Escaper", Job = 1, Level = 60 },
            };
            Players.AddPlayer(Session.SessionId, player);
            
            
            _services.GetRequiredService<EffectService>().RefreshPcTraitSheet(player, player.Character!, Array.Empty<InventoryItem>());
            return player;
        }

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

 /// <summary>Sends a marker frame (opcode 0, body <c>7e57</c>) and returns, in order, every RMI frame the
 /// client receives before it.</summary>
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
