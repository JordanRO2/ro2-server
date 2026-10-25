using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Kill reward and corpse handling: 2393 OnCompensation, the credit and corpse split that LootService performs for the skill path
/// (credit first, 2392 after), and the respawn under a fresh handle that leaves the corpse with its original handle (NpcStateManager.Respawn).
/// </summary>
[Collection(CorpseShareRigCollection.Name)] 
public sealed class KillCreditAndRespawnTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);
    private static string ToHex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    
    [Theory]
    
    [InlineData("01191900c8e211d21a0312852f000000000000e72d000000000000", 0x031AD211u, (byte)18, 12165UL, 11751UL)]
    
    [InlineData("01191900c8e211201c03238dd10000000000008377f9f400000000", 0x031C2011u, (byte)35, 53645UL, 4109989763UL)]
    
    [InlineData("01191900c8e2112e250532000000000000000074754eac00000000", 0x05252E11u, (byte)50, 0UL, 2890823028UL)]
    public void Compensation2393_IsTheRetailFrame(string retail, uint handle, byte level, ulong exp, ulong zeny)
    {
        Assert.Equal(retail, ToHex(CompensationPackets.BuildCompensation2393(handle, level, exp, zeny)));
        Assert.Equal(CompensationPackets.BodyLength, retail.Length / 2);
    }

    [Fact]
    public void Compensation2393_CarriesThePlayersOwnState()
    {
        var player = new PlayerState { EntityId = 0x031AD211u, Level = 18, Exp = 12165, Gold = 11751 };
        Assert.Equal("01191900c8e211d21a0312852f000000000000e72d000000000000",
            ToHex(CompensationPackets.BuildFor(player)));
    }

    
 /// <summary>
 /// ProcessKillCreditAsync sends the kill's quest tick and then the killer's 2393, with no 2392.
 /// ProcessNpcCorpseAsync then opens with the 2392. This is the skill path order (SkillDamageHandler: credit before the killing 2718, corpse after it).
 /// Quest 111010020 (kill 45170000, three times) and the EXP tables are read from data/.
 /// </summary>
    [Fact]
    public async Task KillCredit_GoesOutBeforeTheCorpse_AndNamesNoCorpse()
    {
        const uint quest = 111010020, npcId = 45170000, map = 900041, corpseHandle = 0x00BB4113;
        await using var rig = await Rig.StartAsync();
        var player = new PlayerState
        {
            SessionId = rig.SessionId,
            EntityId = PlayerStateManager.BuildActorHandle(rig.SessionId),
            CharacterId = 941,
            Name = "Tester",
            MapId = map,
            Level = 5,
            Exp = 0,
            Gold = 1234,
        };
        player.ActiveQuests.Add(quest);
        rig.Players.AddPlayer(rig.SessionId, player);

        await rig.Loot.ProcessKillCreditAsync(rig.Session, player, corpseHandle, npcId);

        var tick = await rig.NextRmiAsync();
        Assert.Equal(ZoneS2COpcodes.OnQuestMonsterOrGO, tick.Op);
        Assert.Equal(ToHex(QuestProgressPackets.BuildQuestMonsterOrGo2425(quest, npcId, 1)), ToHex(tick.Body));

        var compensation = await rig.NextRmiAsync();
        Assert.Equal(ZoneS2COpcodes.OnCompensation, compensation.Op);
        var expReward = (ulong)rig.Data.GetNpcStats(npcId)!.EXP_Reward;
        Assert.True(expReward > 0 && expReward < (ulong)rig.Data.GetExpForNextLevel(5)); 
        Assert.Equal(expReward, player.Exp);
        Assert.Equal(ToHex(CompensationPackets.BuildCompensation2393(player.EntityId, 5, expReward, 1234)),
            ToHex(compensation.Body));

        await rig.Loot.ProcessNpcCorpseAsync(rig.Session, player, corpseHandle, npcId);
        var dead = await rig.NextRmiAsync();
        Assert.Equal(ZoneS2COpcodes.OnDeadNotify, dead.Op); 
        Assert.Equal(ToHex(ActorVitalsPackets.BuildDeadNotify2392(corpseHandle, player.EntityId)), ToHex(dead.Body));
    }

    [Fact]
    public async Task Compensation_IsNotSentAtTheLevelCap()
    {
        await using var rig = await Rig.StartAsync();
        var player = new PlayerState { SessionId = rig.SessionId, EntityId = 0x0011, Level = 50 };
        await CompensationPackets.SendAfterKillGrantAsync(rig.Session, player, levelBeforeGrant: 50);
        await CompensationPackets.SendAfterKillGrantAsync(null, player, levelBeforeGrant: 5);
        await rig.Session.SendInternalRmiAsync(ZoneS2COpcodes.OnLevelUp, new byte[] { 0x5A }); 
        var next = await rig.NextRmiAsync();
        Assert.Equal(ZoneS2COpcodes.OnLevelUp, next.Op); 
    }

    
 /// <summary>
 /// A respawn uses a fresh handle while the old corpse may still be on the client, so the corpse keeps the old handle: its loot stays reachable and it can no longer be hit.
 /// The respawned NPC is the same state object under the new handle, and the spawn slot follows it.
 /// </summary>
    [Fact]
    public async Task Respawn_TakesAFreshHandle_AndLeavesTheCorpseItsOwn()
    {
        const uint map = 900042;
        var npcs = await NpcsWithOneSpawnAsync(map, spawnIndex: 7);
        var h1 = npcs.AllocateHandle(map, 7);
        var state = npcs.GetNpc(h1)!;
        Assert.True(npcs.ApplyDamage(h1, 1_000_000).Died);
        var stamp = state.DeathTime!.Value;

        GroundItemService.BeginCorpse(map, h1, state.NpcId, stamp, 42, 1);
        GroundItemService.AddCorpseItem(map, h1, 30000205, 1);
        var pending = LootService.TrackPendingCorpse(h1, stamp);
        try
        {
            Assert.True(LootService.CorpseOnClients(h1, stamp));

            var h2 = npcs.Respawn(h1);

            Assert.NotEqual(h1, h2);
            Assert.Equal(NpcStateManager.NpcActorTag, (byte)h2);
            Assert.Equal(h2, npcs.AllocateHandle(map, 7));   
            Assert.Same(state, npcs.GetNpc(h2));               
            Assert.Equal(h2, state.EntityOid);
            Assert.False(state.IsDead);
            Assert.Equal(state.MaxHp, state.Hp);
            Assert.Null(state.DeathTime);

            var corpse = npcs.GetNpc(h1)!;                      
            Assert.NotSame(state, corpse);
            Assert.True(corpse.IsDead);
            Assert.Equal(stamp, corpse.DeathTime);
            Assert.Equal(h1, corpse.EntityOid);
            Assert.True(GroundItemService.HasCorpseLoot(map, h1, corpse.DeathTime)); 
            Assert.Equal((0, false), npcs.ApplyDamage(h1, 50));                      
            Assert.DoesNotContain(npcs.GetRespawnReady(), n => n.EntityOid == h1);
            Assert.Single(npcs.GetAliveNpcs(map));

            npcs.InitializeMap(map);                             
            Assert.Single(npcs.GetAliveNpcs(map));

            LootService.ReleasePendingCorpse(h1, pending);       
            npcs.RetireCorpse(h1, stamp);
            Assert.Null(npcs.GetNpc(h1));
            Assert.Same(state, npcs.GetNpc(h2));
        }
        finally
        {
            LootService.ReleasePendingCorpse(h1, pending);
            GroundItemService.RemoveCorpse(map, h1);
        }
    }

    [Fact]
    public async Task Respawn_AfterTheCorpseLeft_KeepsNothingUnderTheOldHandle()
    {
        const uint map = 900043;
        var npcs = await NpcsWithOneSpawnAsync(map, spawnIndex: 3);
        var h1 = npcs.AllocateHandle(map, 3);
        Assert.True(npcs.ApplyDamage(h1, 1_000_000).Died);

        var h2 = npcs.Respawn(h1); 

        Assert.NotEqual(h1, h2);
        Assert.Null(npcs.GetNpc(h1));
        Assert.Equal(0u, npcs.Respawn(h1)); 
        Assert.False(npcs.GetNpc(h2)!.IsDead);
    }

    [Fact]
    public void CorpseOnClients_TracksOnlyItsOwnDeath()
    {
        const uint handle = 0x00BB4213;
        var died = new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc);
        var first = LootService.TrackPendingCorpse(handle, died);
        try
        {
            Assert.False(LootService.CorpseOnClients(handle, null));
            Assert.False(LootService.CorpseOnClients(handle, died.AddSeconds(40)));
            Assert.True(LootService.CorpseOnClients(handle, died));
            LootService.ReleasePendingCorpse(handle, first);
            Assert.False(LootService.CorpseOnClients(handle, died));
        }
        finally
        {
            LootService.ReleasePendingCorpse(handle, first);
        }
    }

    
    private static async Task<NpcStateManager> NpcsWithOneSpawnAsync(uint map, int spawnIndex)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ro2-respawn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            
            await File.WriteAllTextAsync(Path.Combine(dir, "ZoneSpawn.json"),
                $$"""[{"MapId":{{map}},"Index":{{spawnIndex}},"PosX":10.5,"PosY":20.25,"PosZ":0,"Rot":0,"AssetIndex":45170000,"HoverRadius":0,"Triggered":0,"SpawnRadius":0}]""");
            var store = new GameDataStore();
            await store.LoadAsync(dir);
            var npcs = new NpcStateManager(store, NullLogger<NpcStateManager>.Instance);
            npcs.InitializeMap(map);
            return npcs;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

 /// <summary>The repository's data/ folder, found by walking up from the test output.</summary>
    private static string RequireDataDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "Quest_Info.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/Quest_Info.json");
    }

    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }

 /// <summary>
 /// A LootService wired to a real ProudServer on loopback with one connected client, whose S2C frames the test reads back.
 /// Loads Quest_Info, BaseExpTable and NpcServerInfo from data/ only (NPCInfo is absent, so there is no drop table and no gatherable corpse).
 /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly ProudServer _server;
        private readonly Socket _client;

        public ProudSession Session { get; }
        public uint SessionId => Session.SessionId;
        public PlayerStateManager Players { get; }
        public GameDataStore Data { get; }
        public LootService Loot { get; }

        private Rig(ProudServer server, Socket client, ProudSession session, GameDataStore data)
        {
            _server = server;
            _client = client;
            Session = session;
            Data = data;
            Players = new PlayerStateManager(new NoDirtySink());
            var broadcast = new BroadcastService(Players, NullLogger<BroadcastService>.Instance);
            broadcast.SetServer(server);
            var npcs = new NpcStateManager(data, NullLogger<NpcStateManager>.Instance);
            var combat = new CombatService(Players, npcs, broadcast, data, NullLogger<CombatService>.Instance);
            var party = new PartyService(Players, broadcast, NullLogger<PartyService>.Instance);
            var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
            Loot = new LootService(data, broadcast, combat, npcs, party, Players, scopes, NullLogger<LootService>.Instance);
        }

        public static async Task<Rig> StartAsync()
        {
            var dir = Path.Combine(Path.GetTempPath(), "ro2-killcredit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            GameDataStore data;
            try
            {
                var source = RequireDataDirectory();
                foreach (var table in new[] { "Quest_Info.json", "BaseExpTable.json", "NpcServerInfo.json" })
                    File.Copy(Path.Combine(source, table), Path.Combine(dir, table));
                data = new GameDataStore();
                await data.LoadAsync(dir);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }

            var server = new ProudServer("test", NullLogger.Instance);
            var connected = new TaskCompletionSource<ProudSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.SessionConnected += s => { connected.TrySetResult(s); return Task.CompletedTask; };
            await server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync(server.LocalEndPoint!);
            var session = await connected.Task.WaitAsync(Generous);
            return new Rig(server, client, session, data);
        }

 /// <summary>The next internal RMI frame the client receives (<c>[13 57][w][len][01][u16 op][body]</c>);
 /// frames of any other message type are skipped.</summary>
        public async Task<(ushort Op, byte[] Body)> NextRmiAsync()
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
                    return (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(1)), data[3..]);
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
