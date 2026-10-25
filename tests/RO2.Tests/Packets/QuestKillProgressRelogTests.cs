using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Kill quest progress across a relog (QuestProgressStore): the quest element of the 2310 login list, and the 2425 counter that continues from it. Quest_Info comes from the shipped data tables.
/// </summary>
public sealed class QuestKillProgressRelogTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);
    private static string ToHex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    private static readonly Lazy<Task<GameDataStore>> QuestTable = new(() => LoadAsync("Quest_Info.json"));

    private static async Task<GameDataStore> LoadAsync(params string[] tables)
    {
        
        var dir = Path.Combine(Path.GetTempPath(), "ro2-questrelog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var source = RequireDataDirectory();
            foreach (var table in tables)
                File.Copy(Path.Combine(source, table), Path.Combine(dir, table));
            var store = new GameDataStore();
            await store.LoadAsync(dir);
            return store;
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

    private static byte[] Element(CharacterQuest quest, GameDataStore? data)
    {
        using var w = new PacketWriter();
        QuestProgressStore.WriteLoginElement(w, quest, data, bag: null);
        return w.ToArray();
    }

    private static CharacterQuest Held(uint questId, params ushort[] kills) => new()
    {
        CharacterId = 941,
        QuestId = questId,
        ProgressJson = kills.Length == 0 ? null : QuestProgressStore.Encode(kills),
    };

    
    [Theory]
    
    [InlineData(212210010u, new byte[] { 4, 5 },
        "02005a11a60c020000000000000004000500000000000000000000000000000000000000000000000000000000000000000000000000")]
    
    [InlineData(212210011u, new byte[] { 5, 5 },
        "02005b11a60c010000000000000005000500000000000000000000000000000000000000000000000000000000000000000000000000")]
    
    
    [InlineData(212210004u, new byte[] { 1 },
        "01005411a60c01000000000000000100000000000000000000000000000000000000")]
    
    [InlineData(641510150u, new byte[] { 7 },
        "010006ab3c2602000000000000000700000000000000000000000000000000000000")]
    
    [InlineData(212210010u, new byte[0],
        "02005a11a60c020000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000")]
    
    
    
    [InlineData(212210019u, new byte[0],
        "01006311a60c02000000000000000000000000000000000000000000000000000000")]
    public async Task LoginElement_IsRetailsElement(uint questId, byte[] kills, string retail)
    {
        var data = await QuestTable.Value;
        Assert.Equal(retail, ToHex(Element(Held(questId, Array.ConvertAll(kills, k => (ushort)k)), data)));
    }

    [Theory]
    
    [InlineData(899990000u, new byte[] { 5, 5, 5 },
        "0300f0c1a4350100000000000000050005000500000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000006d94b23000000000")]
    
    [InlineData(641510070u, new byte[] { 5, 4, 5 },
        "0300b6aa3c26020000000000000005000400050000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000cd90c23000000000")]
    public async Task LoginElement_ThreeRows_MatchesWhatTheClientReads(uint questId, byte[] kills, string retail)
    {
        
        
        
        var data = await QuestTable.Value;
        var ours = Element(Held(questId, Array.ConvertAll(kills, k => (ushort)k)), data);
        var theirs = Convert.FromHexString(retail);
        Assert.Equal(theirs.Length, ours.Length);
        Assert.Equal(ToHex(theirs[..54]), ToHex(ours[..54]));
        Assert.All(ours[54..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void LoginElement_WithoutGameData_KeepsTheOneRecordForm()
    {
        Assert.Equal("01005a11a60c02000000000000000000000000000000000000000000000000000000",
            ToHex(Element(Held(212210010, 4, 5), data: null)));
    }

    [Fact]
    public void ProgressJson_RoundTripsAndToleratesForeignDocuments()
    {
        Assert.Equal("{\"kill\":[4,0,5]}", QuestProgressStore.Encode(new ushort[] { 4, 0, 5 }));
        Assert.Equal(new ushort[] { 4, 0, 5 }, QuestProgressStore.Decode("{\"kill\":[4,0,5]}", 3));
        
        Assert.Equal(new ushort[] { 4, 0 }, QuestProgressStore.Decode("{\"kill\":[4,0,5]}", 2));
        Assert.Equal(new ushort[] { 4, 0, 0, 0 }, QuestProgressStore.Decode("{\"kill\":[4]}", 4));
        
        Assert.Equal(new ushort[] { 108, 431 }, QuestProgressStore.Decode("{\"kill\":[108,431]}", 2));
        
        Assert.Equal(new ushort[] { 65535, 0 }, QuestProgressStore.Decode("{\"kill\":[70000,-1]}", 2));
        Assert.Equal(new ushort[2], QuestProgressStore.Decode("{\"other\":1}", 2));
        Assert.Equal(new ushort[2], QuestProgressStore.Decode("[1,2]", 2));
        Assert.Equal(new ushort[2], QuestProgressStore.Decode("{broken", 2));
        Assert.Equal(new ushort[2], QuestProgressStore.Decode(null, 2));
    }

    
    [Fact]
    public async Task Restore_FillsTheSessionCounters()
    {
        var data = await QuestTable.Value;
        var player = new PlayerState { CharacterId = 941 };
        player.ActiveQuests.Add(212210010);
        QuestProgressStore.RestoreKillProgress(player, Held(212210010, 4, 5), data);
        Assert.Equal((ushort)4, player.QuestKillProgress[212210010]);                       
        Assert.Equal((ushort)5, LootService.ExtraKillCount(player, 212210010, 50003765));    
        Assert.False(LootService.KillObjectiveClosed(player, 212210010));
        Assert.Equal(new ushort[] { 4, 5 }, QuestProgressStore.CurrentKillCounts(player, 212210010, data.QuestRows[212210010]));

        
        QuestProgressStore.RestoreKillProgress(player, Held(212210011, 5, 5), data);
        Assert.True(LootService.KillObjectiveClosed(player, 212210011));

        
        QuestProgressStore.RestoreKillProgress(player, Held(641510150), data);
        Assert.False(player.QuestKillProgress.ContainsKey(641510150));
    }

 /// <summary>
 /// A kill quest's progress is stored by the live kill path (LootService.ProcessKillCreditAsync) and loaded again at the next login as the 2310 element.
 /// </summary>
    [Fact]
    public async Task KillAfterRestore_CountsOnFromThe2310_AndIsStored()
    {
        await using var rig = await Rig.StartAsync();
        var player = rig.NewPlayer();
        player.ActiveQuests.Add(212210010);
        QuestProgressStore.RestoreKillProgress(player, Held(212210010, 4, 5), rig.Data);

        await rig.Loot.ProcessKillCreditAsync(rig.Session, player, 0x00BB4213, 50003765);
        var tick = await rig.NextRmiAsync();
        Assert.Equal(ZoneS2COpcodes.OnQuestMonsterOrGO, tick.Op);
        Assert.Equal("01121200dbe3000000005a11a60c35fffa020600", ToHex(tick.Body));
        Assert.Equal((941u, 212210010u, "{\"kill\":[4,6],\"met\":[0,0]}"), Assert.Single(rig.Quests.Saves));

        await rig.Loot.ProcessKillCreditAsync(rig.Session, player, 0x00BB4313, 50003757);
        tick = await rig.NextRmiUntilAsync(ZoneS2COpcodes.OnQuestMonsterOrGO);
        Assert.Equal("01121200dbe3000000005a11a60c2dfffa020500", ToHex(tick.Body));
        Assert.Equal((941u, 212210010u, "{\"kill\":[5,6],\"met\":[0,0]}"), rig.Quests.Saves[^1]);

        
        var reloaded = new CharacterQuest { CharacterId = 941, QuestId = 212210010, ProgressJson = rig.Quests.Saves[^1].Json };
        Assert.Equal("02005a11a60c020000000000000005000600", ToHex(Element(reloaded, rig.Data)[..18]));
    }

 /// <summary>A kill quest loaded as already met gets no 2425 at a kill of its target, and nothing is stored.</summary>
    [Fact]
    public async Task KillOfAMetQuest_SendsNoTick()
    {
        await using var rig = await Rig.StartAsync();
        var player = rig.NewPlayer();
        player.ActiveQuests.Add(212210011);
        QuestProgressStore.RestoreKillProgress(player, Held(212210011, 5, 5), rig.Data);

        await rig.Loot.ProcessKillCreditAsync(rig.Session, player, 0x00BB4413, 50003751);
        await rig.Session.SendInternalRmiAsync(ZoneS2COpcodes.OnLevelUp, new byte[] { 0x5A }); 
        var next = await rig.NextRmiAsync();
        while (next.Op != ZoneS2COpcodes.OnLevelUp || next.Body is not [0x5A])
        {
            Assert.NotEqual(ZoneS2COpcodes.OnQuestMonsterOrGO, next.Op);
            Assert.NotEqual(ZoneS2COpcodes.OnQuestCompleteNotify, next.Op);
            next = await rig.NextRmiAsync();
        }
        Assert.Empty(rig.Quests.Saves);
    }

    
 /// <summary>Records <see cref="IQuestRepository.SaveProgressAsync"/>; nothing else is used by the kill path.</summary>
    private sealed class RecordingQuests : IQuestRepository
    {
        public List<(uint CharacterId, uint QuestId, string? Json)> Saves { get; } = new();

        public Task SaveProgressAsync(uint characterId, uint questId, string? progressJson)
        {
            lock (Saves) Saves.Add((characterId, questId, progressJson));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CharacterQuest>> GetActiveQuestsAsync(uint characterId)
            => Task.FromResult<IReadOnlyList<CharacterQuest>>(Array.Empty<CharacterQuest>());
        public Task<IReadOnlyList<uint>> GetCompletedQuestIdsAsync(uint characterId)
            => Task.FromResult<IReadOnlyList<uint>>(Array.Empty<uint>());
        public Task<DateTime?> GetLastCompletedAtAsync(uint characterId, uint questId) => Task.FromResult<DateTime?>(null);
        public Task AcceptQuestAsync(uint characterId, uint questId) => Task.CompletedTask;
        public Task CompleteQuestAsync(uint characterId, uint questId) => Task.CompletedTask;
        public Task GiveUpQuestAsync(uint characterId, uint questId) => Task.CompletedTask;
        public Task SetQuestsTracedAsync(uint characterId, IReadOnlyCollection<uint> tracedQuestIds) => Task.CompletedTask;
    }

    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }

 /// <summary>A LootService on a loopback ProudServer with one connected client, using <see cref="RecordingQuests"/> as the quest repository.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly ProudServer _server;
        private readonly Socket _client;

        public ProudSession Session { get; }
        public PlayerStateManager Players { get; }
        public GameDataStore Data { get; }
        public LootService Loot { get; }
        public RecordingQuests Quests { get; } = new();

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
            var scopes = new ServiceCollection().AddSingleton<IQuestRepository>(Quests).BuildServiceProvider()
                .GetRequiredService<IServiceScopeFactory>();
            Loot = new LootService(data, broadcast, combat, npcs, party, Players, scopes, NullLogger<LootService>.Instance);
        }

        public PlayerState NewPlayer()
        {
            var player = new PlayerState
            {
                SessionId = Session.SessionId,
                EntityId = PlayerStateManager.BuildActorHandle(Session.SessionId),
                CharacterId = 941,
                Name = "Tester",
                MapId = 900041,
                Level = 5,
            };
            Players.AddPlayer(Session.SessionId, player);
            return player;
        }

        public static async Task<Rig> StartAsync()
        {
            var data = await LoadAsync("Quest_Info.json", "BaseExpTable.json", "NpcServerInfo.json");
            var server = new ProudServer("test", NullLogger.Instance);
            var connected = new TaskCompletionSource<ProudSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.SessionConnected += s => { connected.TrySetResult(s); return Task.CompletedTask; };
            await server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync(server.LocalEndPoint!);
            var session = await connected.Task.WaitAsync(Generous);
            return new Rig(server, client, session, data);
        }

        public async Task<(ushort Op, byte[] Body)> NextRmiUntilAsync(ushort op)
        {
            while (true)
            {
                var next = await NextRmiAsync();
                if (next.Op == op) return next;
            }
        }

 /// <summary>The next internal RMI frame the client receives (<c>[13 57][w][len][01][u16 op][body]</c>). Frames of any other message type are skipped.</summary>
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
