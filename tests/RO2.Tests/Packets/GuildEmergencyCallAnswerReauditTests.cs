using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData.Tables;
using RO2.Domain.GameData;
using RO2.Identity.Application.Commands;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Login.Handlers.Rmi;
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

namespace RO2.Tests.Packets;

/// <summary>
/// 1653 ReqGuildEmergencyCallAnswer: what a cross-map answer sends before the move
/// (GuildEmergencyCallAnswerHandler.DepartCrossMapAsync), over a loopback session.
/// </summary>
/// <remarks>
/// Shares the MountService state collection with RidingServiceTests and WarpDepartureTests: the departure
/// drops the ride, and MountService is process-wide and keyed by session id.
/// </remarks>
[Collection(MountStateCollection.Name)]
public sealed class GuildEmergencyCallAnswerReauditTests
{
 /// <summary>
 /// Frame order of a cross-map answer, shaped on a reference answer on map 3201 with effects 40013 and 40000
 /// active. The 2315 opens the frames, at the last 1526 position, ahead of the removal of 40013 (still listed
 /// at that point), then the removal, 2938 and the self 2505. This holds in all 17 reference cross-map answers.
 /// The opening record is the shared builder's, compared byte for byte with the one built from the PlayerState
 /// before the departure.
 /// </summary>
    [Fact]
    public async Task CrossMapAnswer_OpensWithTheSelf2315_BeforeAnyRemoval()
    {
        var data = new GameDataStore();
        await data.LoadAsync(RequireDataDirectory());
        await using var rig = await Rig.StartAsync(data, spareSessions: 14);
        var player = rig.AddPlayer(0x088AAA11u, 3201, 1280.3f, 1280.5f, 198.2f);
        player.ActiveEffects.Add(new ActiveEffect { EffectId = 40013, EffectLevel = 1, TargetHandle = player.EntityId });
        player.ActiveEffects.Add(new ActiveEffect { EffectId = 40000, EffectLevel = 1, TargetHandle = player.EntityId });

        
        var opening = Convert.ToHexString(ActorStatePackets.BuildOnPCViewData2315(player, player.Character!,
            Array.Empty<InventoryItem>(), TestEmblems.Create())).ToLowerInvariant();

        await GuildEmergencyCallAnswerHandler.DepartCrossMapAsync(rig.Session, player, rig.Services,
            NullLogger.Instance, CancellationToken.None);
        var frames = await rig.FramesUntilMarkerAsync();

        Assert.Equal((ZoneS2COpcodes.OnPCViewData, opening), frames[0]);
        var ops = frames.Select(f => f.Op).ToList();
        
        Assert.Equal(new[] { ZoneS2COpcodes.OnPcTrait, ZoneS2COpcodes.OnDelEffectSuccess, ZoneS2COpcodes.OnDelEffect },
            ops.Skip(1).Take(3).ToArray());
        int ack = ops.IndexOf(ZoneS2COpcodes.AnsSiegeArmsUseEnd);
        Assert.True(ack > 3);
        Assert.Equal(ZoneS2COpcodes.OnPC_Out, ops[ack + 1]);
        Assert.Equal(1, ops.Count(o => o == ZoneS2COpcodes.OnPCViewData));

        
        
        var after = ActorStatePackets.BuildOnPCViewData2315(player, player.Character!, Array.Empty<InventoryItem>(), TestEmblems.Create());
        Assert.Equal(after.Length + 23, opening.Length / 2);

        
        var body = Convert.FromHexString(opening);
        var blob = body.AsSpan(1 + body[0]);
        Assert.Equal(58007, BinaryPrimitives.ReadUInt16LittleEndian(blob[2..]));
        Assert.Equal(0x088AAA11u, BinaryPrimitives.ReadUInt32LittleEndian(blob[8..]));
        Assert.Equal(1280.3f, BinaryPrimitives.ReadSingleLittleEndian(blob[26..]));
        Assert.Equal(1280.5f, BinaryPrimitives.ReadSingleLittleEndian(blob[30..]));
        Assert.Equal(198.2f, BinaryPrimitives.ReadSingleLittleEndian(blob[34..]));
        Assert.Equal(0, blob[24]);
    }

    private static string RequireDataDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "Effect_Table.json")))
                return candidate;
        }
        throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/Effect_Table.json");
    }

 /// <summary>
 /// A real <see cref="ProudSession"/> on a loopback <see cref="ProudServer"/>, read from the client end, with
 /// the singletons the departure resolves (GameDataStore, BroadcastService, EffectService, PlayerSpawnService,
 /// an empty inventory) and no database. <c>spareSessions</c> connections are opened first so the
 /// session id is not one another rig of the collection has.
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
            var broadcast = new BroadcastService(Players, NullLogger<BroadcastService>.Instance);
            broadcast.SetServer(server);
            var players = Players;
            var collection = new ServiceCollection();
            collection.AddSingleton(data);
            collection.AddSingleton(broadcast);
            collection.AddSingleton<IInventoryRepository>(new NoGearInventory());
            collection.AddSingleton(TestEmblems.Create());
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

 /// <summary>An in-world PC, alone on its map, registered for the broadcasts.</summary>
        public PlayerState AddPlayer(uint entityId, uint mapId, float x, float y, float z)
        {
            var player = new PlayerState
            {
                SessionId = Session.SessionId, EntityId = entityId, CharacterId = 915_653, Name = "Answerer",
                Level = 60, MapId = mapId, MapLoaded = true, Hp = 5000, MaxHp = 5000, Mp = 500, MaxMp = 500,
                X = x, Y = y, Z = z,
                Character = new Character { Id = 915_653, Name = "Answerer", Job = 1, Level = 60 },
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
