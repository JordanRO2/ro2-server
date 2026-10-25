using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Inventory.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// Tests for the PC's 2315 OnPCViewData that an effect removal re-sends when the removed effect moves the PC's max HP or max SP.
/// </summary>
/// <remarks>
/// A self removal whose 2498 moved the max is followed by the PC's 2315, between that 2498 and the 2493. The 2315's max HP and max SP equal the 2498's. Removals of effects with no max row send no 2315. The record itself is pinned by OnPcViewData2315RecordTests; these tests pin the trigger, the order, and the recipients (the owner and the viewers on the map, as with the 2494). The trigger compares the sheet with the effect against the sheet without it, never the stored PlayerState.MaxHp or MaxMp, so a stored max that no sheet set cannot turn the removal of an effect with no max row into a 2315. The 2498 bodies come from this server's own sheet, so they are compared on their max fields only.
/// </remarks>
public sealed class Reaudit3_A0_Tests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);
    private static readonly Lazy<Task<GameDataStore>> Data = new(LoadDataAsync);

    private const uint OwnerHandle = 0x0D790911u, ViewerHandle = 0x0D818911u;

 /// <summary>
 /// Effect 105207 (a buff with trait 21 +500 and trait 22 +300 rows) removed by the server: the owner gets 2498, 2315, 2493 and 2494; a viewer on the same map gets the same 2315 and the 2494.
 /// </summary>
    [Fact]
    public async Task RemovalThatMovesTheMax_SendsThePcRecordBetweenTheSheetAndThe2493()
    {
        var data = await Data.Value;
        await using var rig = await Rig.StartAsync(data);
        var (owner, _) = rig.AddPlayers();
        owner.ActiveEffects.Add(new ActiveEffect { EffectId = 105207, EffectLevel = 1, TargetHandle = owner.EntityId });
        rig.SyncSheet(owner);   
        int buffedMaxHp = owner.MaxHp, buffedMaxSp = owner.MaxMp;

        Assert.True(await rig.Effects.RemovePlayerEffectAsync(owner, 105207));
        var ownerFrames = await rig.FramesUntilMarkerAsync(rig.OwnerSession, rig.OwnerClient);
        var viewerFrames = await rig.FramesUntilMarkerAsync(rig.ViewerSession, rig.ViewerClient);

        Assert.Equal(new[] { ZoneS2COpcodes.OnPcTrait, ZoneS2COpcodes.OnPCViewData, ZoneS2COpcodes.OnDelEffectSuccess,
            ZoneS2COpcodes.OnDelEffect }, ownerFrames.Select(f => f.Op).ToArray());
        Assert.Equal(new[] { ZoneS2COpcodes.OnPCViewData, ZoneS2COpcodes.OnDelEffect },
            viewerFrames.Select(f => f.Op).ToArray());

        
        Assert.True(owner.MaxHp < buffedMaxHp);
        Assert.True(owner.MaxMp < buffedMaxSp);
        var sheet = Blob(ownerFrames[0].Body);
        var record = Blob(ownerFrames[1].Body);
        Assert.Equal((uint)owner.MaxHp, BinaryPrimitives.ReadUInt32LittleEndian(sheet.AsSpan(TraitPackets.PcTraitMaxHpOffset)));
        Assert.Equal(BinaryPrimitives.ReadUInt32LittleEndian(sheet.AsSpan(TraitPackets.PcTraitMaxHpOffset)),
            BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(57)));
        Assert.Equal(BinaryPrimitives.ReadUInt16LittleEndian(sheet.AsSpan(TraitPackets.PcTraitMaxSpOffset)),
            BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(65)));
        Assert.Equal(58007, BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(2)));
        Assert.Equal(OwnerHandle, BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(8)));

        
        
        var expected = Hex(ActorStatePackets.BuildOnPCViewData2315(owner, owner.Character!, Array.Empty<InventoryItem>(), TestEmblems.Create()));
        Assert.Equal(expected, ownerFrames[1].Body);
        Assert.Equal(expected, viewerFrames[0].Body);

        
        Assert.Equal(Hex(EffectPackets.BuildDelEffectSuccess2493(105207)), ownerFrames[2].Body);
        Assert.Equal(Hex(ActorVitalsPackets.BuildDelEffect2494(105207, OwnerHandle)), ownerFrames[3].Body);
        Assert.Equal(ownerFrames[3].Body, viewerFrames[1].Body);
    }

 /// <summary>
 /// Effect 60205 (a map-move buff with miss and guard rows, and no max row) leaves the max unchanged: the removal sends 2498, 2493 and 2494, and no 2315.
 /// </summary>
    [Fact]
    public async Task RemovalThatLeavesTheMax_SendsNoRecord()
    {
        var data = await Data.Value;
        await using var rig = await Rig.StartAsync(data);
        var (owner, _) = rig.AddPlayers();
        owner.ActiveEffects.Add(new ActiveEffect { EffectId = 60205, EffectLevel = 1, TargetHandle = owner.EntityId });
        rig.SyncSheet(owner);
        int maxHp = owner.MaxHp, maxSp = owner.MaxMp;

        Assert.True(await rig.Effects.RemovePlayerEffectAsync(owner, 60205));

        Assert.Equal(new[] { ZoneS2COpcodes.OnPcTrait, ZoneS2COpcodes.OnDelEffectSuccess, ZoneS2COpcodes.OnDelEffect },
            (await rig.FramesUntilMarkerAsync(rig.OwnerSession, rig.OwnerClient)).Select(f => f.Op).ToArray());
        Assert.Equal(new[] { ZoneS2COpcodes.OnDelEffect },
            (await rig.FramesUntilMarkerAsync(rig.ViewerSession, rig.ViewerClient)).Select(f => f.Op).ToArray());
        Assert.Equal((maxHp, maxSp), (owner.MaxHp, owner.MaxMp));
    }

 /// <summary>
 /// The live expiry path with two max-moving buffs due together: they leave ActiveEffects in one step, and each removal still carries its 2315, because each effect moves the max against the set still active. Both records show the PC after the burst, listing neither effect.
 /// </summary>
    [Fact]
    public async Task ExpiryBurst_CarriesARecordOnEachRemovalWhoseEffectMovesTheMax()
    {
        var data = await Data.Value;
        await using var rig = await Rig.StartAsync(data);
        var (owner, _) = rig.AddPlayers();
        var past = DateTime.UtcNow.AddSeconds(-1);
        owner.ActiveEffects.Add(new ActiveEffect { EffectId = 105207, EffectLevel = 1, TargetHandle = owner.EntityId, ExpiresAt = past });
        owner.ActiveEffects.Add(new ActiveEffect { EffectId = 105202, EffectLevel = 1, TargetHandle = owner.EntityId, ExpiresAt = past });
        rig.SyncSheet(owner);

        await rig.RunExpiryTickAsync(DateTime.UtcNow);
        var ownerFrames = await rig.FramesUntilMarkerAsync(rig.OwnerSession, rig.OwnerClient);
        var viewerFrames = await rig.FramesUntilMarkerAsync(rig.ViewerSession, rig.ViewerClient);

        Assert.Equal(new[]
        {
            ZoneS2COpcodes.OnPcTrait, ZoneS2COpcodes.OnPCViewData, ZoneS2COpcodes.OnDelEffectSuccess, ZoneS2COpcodes.OnDelEffect,
            ZoneS2COpcodes.OnPcTrait, ZoneS2COpcodes.OnPCViewData, ZoneS2COpcodes.OnDelEffectSuccess, ZoneS2COpcodes.OnDelEffect,
        }, ownerFrames.Select(f => f.Op).ToArray());
        Assert.Equal(new[] { ZoneS2COpcodes.OnPCViewData, ZoneS2COpcodes.OnDelEffect, ZoneS2COpcodes.OnPCViewData,
            ZoneS2COpcodes.OnDelEffect }, viewerFrames.Select(f => f.Op).ToArray());
        lock (owner.ActiveEffects)
            Assert.Empty(owner.ActiveEffects);
        var expected = Hex(ActorStatePackets.BuildOnPCViewData2315(owner, owner.Character!, Array.Empty<InventoryItem>(), TestEmblems.Create()));
        Assert.Equal(expected, ownerFrames[1].Body);
        Assert.Equal(expected, ownerFrames[5].Body);
    }

 /// <summary>
 /// A burst of two buffs ending together: 40012 (attack traits) and then 40011 (+10 % max HP). 40012 sends 2498, 2493 and 2494; 40011 sends 2498, 2315, 2493 and 2494. The stored max has already dropped by the 40012 removal, and the 2315 still lands on the change made by 40011.
 /// </summary>
    [Fact]
    public async Task GuildBuffBurst_PutsTheRecordOnTheMaxHpBuffOnly()
    {
        var data = await Data.Value;
        await using var rig = await Rig.StartAsync(data);
        var (owner, _) = rig.AddPlayers();
        var past = DateTime.UtcNow.AddSeconds(-1);
        
        owner.ActiveEffects.Add(new ActiveEffect { EffectId = 40011, EffectLevel = 1, TargetHandle = owner.EntityId, ExpiresAt = past });
        owner.ActiveEffects.Add(new ActiveEffect { EffectId = 40012, EffectLevel = 1, TargetHandle = owner.EntityId, ExpiresAt = past });
        rig.SyncSheet(owner);
        int buffedMaxHp = owner.MaxHp;

        await rig.RunExpiryTickAsync(DateTime.UtcNow);
        var ownerFrames = await rig.FramesUntilMarkerAsync(rig.OwnerSession, rig.OwnerClient);

        Assert.Equal(new[]
        {
            ZoneS2COpcodes.OnPcTrait, ZoneS2COpcodes.OnDelEffectSuccess, ZoneS2COpcodes.OnDelEffect,
            ZoneS2COpcodes.OnPcTrait, ZoneS2COpcodes.OnPCViewData, ZoneS2COpcodes.OnDelEffectSuccess, ZoneS2COpcodes.OnDelEffect,
        }, ownerFrames.Select(f => f.Op).ToArray());
        Assert.Equal(Hex(EffectPackets.BuildDelEffectSuccess2493(40012)), ownerFrames[1].Body);
        Assert.Equal(Hex(EffectPackets.BuildDelEffectSuccess2493(40011)), ownerFrames[5].Body);
        Assert.True(owner.MaxHp < buffedMaxHp);
    }

 /// <summary>
 /// Removals made by the riding, warp-departure and emergency-call paths (effects 100, 40013 and 60205) send no 2315, even when the stored max is not the sheet's. The removal's 2498 resets the stored max to the sheet's value, which is not a max change of the effect.
 /// </summary>
    [Theory]
    [InlineData(100u)]
    [InlineData(40013u)]
    [InlineData(60205u)]
    public async Task RemovalOfAnEffectWithNoMaxRow_SendsNoRecord_WhateverTheStoredMax(uint effectId)
    {
        var data = await Data.Value;
        await using var rig = await Rig.StartAsync(data);
        var (owner, _) = rig.AddPlayers();
        owner.ActiveEffects.Add(new ActiveEffect { EffectId = effectId, EffectLevel = 1, TargetHandle = owner.EntityId });
        owner.Hp = owner.MaxHp = 5000;
        owner.Mp = owner.MaxMp = 500;

        Assert.True(await rig.Effects.RemovePlayerEffectAsync(owner, effectId));

        Assert.Equal(new[] { ZoneS2COpcodes.OnPcTrait, ZoneS2COpcodes.OnDelEffectSuccess, ZoneS2COpcodes.OnDelEffect },
            (await rig.FramesUntilMarkerAsync(rig.OwnerSession, rig.OwnerClient)).Select(f => f.Op).ToArray());
        Assert.Equal(new[] { ZoneS2COpcodes.OnDelEffect },
            (await rig.FramesUntilMarkerAsync(rig.ViewerSession, rig.ViewerClient)).Select(f => f.Op).ToArray());
        Assert.NotEqual(5000, owner.MaxHp);   
    }

 /// <summary>
 /// Adding the same buff sends its 2499 run, one 2498 and the 2492, and no 2315. The max rises with the sheet.
 /// </summary>
    [Fact]
    public async Task AddThatMovesTheMax_SendsNoRecord()
    {
        var data = await Data.Value;
        await using var rig = await Rig.StartAsync(data);
        var (owner, _) = rig.AddPlayers();
        int maxHp = owner.MaxHp;

        await rig.Effects.ApplyEffectToPlayerAsync(owner, 105207, sourceSkillId: 0, casterHandle: owner.EntityId,
            casterName: owner.Name, durationMs: 600_000);
        var ownerOps = (await rig.FramesUntilMarkerAsync(rig.OwnerSession, rig.OwnerClient)).Select(f => f.Op).ToList();
        var viewerOps = (await rig.FramesUntilMarkerAsync(rig.ViewerSession, rig.ViewerClient)).Select(f => f.Op).ToList();

        Assert.True(owner.MaxHp > maxHp);
        Assert.Contains(ZoneS2COpcodes.OnPcTrait, ownerOps);
        Assert.Equal(ZoneS2COpcodes.OnAddEffect, ownerOps[^1]);
        Assert.DoesNotContain(ZoneS2COpcodes.OnPCViewData, ownerOps);
        Assert.DoesNotContain(ZoneS2COpcodes.OnPCViewData, viewerOps);
    }

    
    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

 /// <summary>The blob of a COMPACT-envelope body (<c>[u8 n][n-byte length][blob]</c>), from its hex.</summary>
    private static byte[] Blob(string bodyHex)
    {
        var body = Convert.FromHexString(bodyHex);
        int n = body[0];
        int length = n switch
        {
            1 => body[1],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(1)),
            _ => BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(1)),
        };
        Assert.Equal(1 + n + length, body.Length);
        return body.AsSpan(1 + n, length).ToArray();
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
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "Effect_Table.json")))
                return candidate;
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
 /// Two real ProudSessions on a loopback ProudServer (the owner and a viewer on the same map), read from their client ends, with a real EffectService over a real BroadcastService and an empty inventory. Twenty spare connections are opened first so that session ids 21 and 22 belong to this rig alone, since the 2315 record reads the PC's ride from MountService, which is process-wide and keyed by session id.
 /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private const int Spare = 20;
        private readonly ProudServer _server;
        private readonly Socket[] _spare;
        private readonly ServiceProvider _services;
        private readonly PlayerStateManager _players;

        public ProudSession OwnerSession { get; }
        public ProudSession ViewerSession { get; }
        public Socket OwnerClient { get; }
        public Socket ViewerClient { get; }
        public EffectService Effects { get; }

        private Rig(ProudServer server, Socket[] spare, Socket ownerClient, ProudSession owner, Socket viewerClient,
            ProudSession viewer, GameDataStore data)
        {
            _server = server;
            _spare = spare;
            OwnerClient = ownerClient;
            OwnerSession = owner;
            ViewerClient = viewerClient;
            ViewerSession = viewer;
            _players = new PlayerStateManager(new NoDirtySink());
            var broadcast = new BroadcastService(_players, NullLogger<BroadcastService>.Instance);
            broadcast.SetServer(server);
            var players = _players;
            var collection = new ServiceCollection();
            collection.AddSingleton(data);
            collection.AddSingleton<IInventoryRepository>(new NoGearInventory());
            collection.AddSingleton(sp => new EffectService(players, broadcast, data, npcState: null!, loot: null!,
                sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<EffectService>.Instance, emblems: TestEmblems.Create()));
            _services = collection.BuildServiceProvider();
            Effects = _services.GetRequiredService<EffectService>();
        }

        public static async Task<Rig> StartAsync(GameDataStore data)
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
            var spare = new Socket[Spare];
            for (int i = 0; i < Spare; i++)
            {
                spare[i] = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await spare[i].ConnectAsync(server.LocalEndPoint!);
                Assert.True(await connected.WaitAsync(Generous));
            }
            var ownerClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await ownerClient.ConnectAsync(server.LocalEndPoint!);
            Assert.True(await connected.WaitAsync(Generous));
            var viewerClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await viewerClient.ConnectAsync(server.LocalEndPoint!);
            Assert.True(await connected.WaitAsync(Generous));
            ProudSession owner, viewer;
            lock (sessions)
            {
                owner = sessions[^2];
                viewer = sessions[^1];
            }
            Assert.Equal((uint)Spare + 1, owner.SessionId);
            Assert.Equal((uint)Spare + 2, viewer.SessionId);
            MountService.Dismount(owner.SessionId);
            MountService.Dismount(viewer.SessionId);
            return new Rig(server, spare, ownerClient, owner, viewerClient, viewer, data);
        }

 /// <summary>The owner and a viewer, both in world on map 1100, each with its server max set by its sheet, as the
 /// live 1304 sets it (EffectService.RefreshMapLoadPcTraitSheet).</summary>
        public (PlayerState Owner, PlayerState Viewer) AddPlayers()
        {
            var owner = NewPlayer(OwnerSession.SessionId, OwnerHandle, 915_901, "Holder");
            var viewer = NewPlayer(ViewerSession.SessionId, ViewerHandle, 915_902, "Watcher");
            _players.AddPlayer(OwnerSession.SessionId, owner);
            _players.AddPlayer(ViewerSession.SessionId, viewer);
            SyncSheet(owner);
            SyncSheet(viewer);
            return (owner, viewer);
        }

 /// <summary>Sets the PC's server max to its sheet with the effects now in ActiveEffects, sending nothing.</summary>
        public void SyncSheet(PlayerState player)
            => Effects.RefreshPcTraitSheet(player, player.Character!, Array.Empty<InventoryItem>());

 /// <summary>One pass of the service's own expiry/tick loop (EffectService.ProcessPlayerEffectsAsync, which its
 /// ExecuteAsync runs every tick), at <paramref name="now"/>.</summary>
        public async Task RunExpiryTickAsync(DateTime now)
        {
            var method = typeof(EffectService).GetMethod("ProcessPlayerEffectsAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(nameof(EffectService), "ProcessPlayerEffectsAsync");
            await (Task)method.Invoke(Effects, new object[] { now })!;
        }

        private static PlayerState NewPlayer(uint sessionId, uint handle, uint characterId, string name) => new()
        {
            SessionId = sessionId, EntityId = handle, CharacterId = characterId, Name = name,
            Level = 60, MapId = 1100, MapLoaded = true, Hp = 1, MaxHp = 1, Mp = 1, MaxMp = 1,
            Character = new Character { Id = characterId, Name = name, Job = 1, Level = 60 },
        };

 /// <summary>Sends a marker frame (opcode 0, body <c>7e57</c>) to <paramref name="session"/> and returns, in order,
 /// every RMI frame its client end receives before it.</summary>
        public async Task<List<(ushort Op, string Body)>> FramesUntilMarkerAsync(ProudSession session, Socket client)
        {
            await session.SendInternalRmiAsync(0, new byte[] { 0x7E, 0x57 });
            var frames = new List<(ushort Op, string Body)>();
            while (true)
            {
                var frame = await NextRmiHexAsync(client);
                if (frame == (0, "7e57")) return frames;
                frames.Add(frame);
            }
        }

 /// <summary>The next internal RMI frame (<c>[13 57][w][len][01][u16 op][body]</c>) as (opcode, body hex);
 /// frames of any other message type are skipped.</summary>
        private static async Task<(ushort Op, string Body)> NextRmiHexAsync(Socket client)
        {
            while (true)
            {
                var head = await ReadExactAsync(client, 3);
                Assert.Equal(0x13, head[0]);
                Assert.Equal(0x57, head[1]);
                var lengthBytes = await ReadExactAsync(client, head[2]);
                int length = head[2] switch
                {
                    1 => lengthBytes[0],
                    2 => BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes),
                    _ => BinaryPrimitives.ReadInt32LittleEndian(lengthBytes),
                };
                var data = await ReadExactAsync(client, length);
                if (data.Length >= 3 && data[0] == 0x01)
                    return (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(1)),
                        Convert.ToHexString(data, 3, data.Length - 3).ToLowerInvariant());
            }
        }

        private static async Task<byte[]> ReadExactAsync(Socket client, int count)
        {
            var buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = await client.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None).AsTask().WaitAsync(Generous);
                if (n == 0) throw new EndOfStreamException("connection closed mid-frame");
                read += n;
            }
            return buffer;
        }

        public async ValueTask DisposeAsync()
        {
            MountService.Dismount(OwnerSession.SessionId);
            MountService.Dismount(ViewerSession.SessionId);
            OwnerClient.Dispose();
            ViewerClient.Dispose();
            foreach (var s in _spare) s.Dispose();
            await _services.DisposeAsync();
            await _server.StopAsync();
        }
    }
}
