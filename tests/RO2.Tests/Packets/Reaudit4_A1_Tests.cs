using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Tests for the live base EXP in the 2391 head (head+70), the 5-then-1 2398 that closes an engagement a monster survived, and the 2472 and 2488 burst sent on a guild kick.
/// </summary>
/// <remarks>
/// The sweep tests pin this server's order and bytes against the form of the reference frames. The 322 ms timing is the median of the measured state-5 to state-1 gaps, not a per-frame match.
/// </remarks>
public class Reaudit4_A1_Tests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);
    private static readonly Lazy<Task<GameDataStore>> Data = new(LoadDataAsync);

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    private static Character Hero() => new()
    {
        Id = 915_610, Name = "Binder", Job = 1, Level = 30, Gender = 1, Race = 1, Exp = 100,
        BindIndex = 103,
    };

    
 /// <summary>
 /// The first 86 bytes of a 2391 frame: [u32 innerLen][u32 handle][head 0..77]. A sub-cap death sample has head+69 = 27 and a within-level EXP of 8,281 at head+70.
 /// </summary>
    private const string Retail2391Prefix =
        "460400001131ec0e371110a8b3b6e00d1131ec0e520049004b0041007300680069006e0061007a0061006b0069003300000000000000"
        + "00000000000000000202030eff00080000000601ff00011b5920000000000000";

    [Fact]
    public void Retail2391_HeadOffsets_LevelAt69_BaseExpAt70()
    {
        var body = Convert.FromHexString(Retail2391Prefix);
        var head = body.AsSpan(8);
        Assert.Equal(27, head[69]);
        Assert.Equal(8_281UL, BinaryPrimitives.ReadUInt64LittleEndian(head[70..]));
    }

    [Fact]
    public void DeadMe2391_StampsTheLiveExp_OverTheLoginSnapshot()
    {
        var victim = new PlayerState
        {
            EntityId = 0x0EEC3111, CharacterUid = 0x0DE0B6B3A8101137UL, Level = 27, Exp = 8_281,
            MapId = 1100, Character = Hero(),     
        };
        var body = PlayerDeathPackets.BuildOnDeadMe2391(victim, [], talentPoints: 0);
        var head = body.AsSpan(8, 205);
        Assert.Equal(27, head[69]);
        Assert.Equal(8_281UL, BinaryPrimitives.ReadUInt64LittleEndian(head[70..]));

        
        victim.Character = null;
        var bare = PlayerDeathPackets.BuildOnDeadMe2391(victim, [], talentPoints: 0);
        Assert.Equal(8_281UL, BinaryPrimitives.ReadUInt64LittleEndian(bare.AsSpan(8 + 70)));
    }

 /// <summary>
 /// At the level cap head+70 is 0, while GrantExpAsync keeps banking EXP into PlayerState.Exp at level 50.
 /// </summary>
    [Fact]
    public void DeadMe2391_AtTheLevelCap_CarriesZeroExp_NotTheBankedOverflow()
    {
        var victim = new PlayerState
        {
            EntityId = 0x0EEC3111, CharacterUid = 0x0DE0B6B3A8101137UL,
            Level = (byte)CompensationPackets.LevelCap, Exp = 9_876_543,   
            MapId = 1100, Character = Hero(),
        };
        var head = PlayerDeathPackets.BuildOnDeadMe2391(victim, [], talentPoints: 0).AsSpan(8, 205);
        Assert.Equal(CompensationPackets.LevelCap, head[69]);
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(head[70..]));

        victim.Character = null;
        var bare = PlayerDeathPackets.BuildOnDeadMe2391(victim, [], talentPoints: 0);
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(bare.AsSpan(8 + 70)));

        
        victim.Level = (byte)(CompensationPackets.LevelCap - 1);
        Assert.Equal(9_876_543UL, PlayerDeathPackets.BaseExpForHead(victim));
    }

    
 /// <summary>The state-1 2398 that follows a state-5 one carries blob+14 0, blob+19 3 and target 0.</summary>
    private const string RetailState5 = "011b1b00eae2000000001316f900050000000000000300000000000000";
    private const string RetailState1 = "011b1b00eae2000000001316f900010000000000000300000000000000";

    [Fact]
    public void ChangeState2398_ReturnAndLeave_AreRetailBytes()
    {
        Assert.Equal(5, CombatPackets.ActorState.NpcReturn);
        Assert.Equal(RetailState5,
            Hex(CombatPackets.BuildChangeState2398(0x00F91613, CombatPackets.ActorState.NpcReturn, trailing: 3)));
        Assert.Equal(RetailState1,
            Hex(CombatPackets.BuildChangeState2398(0x00F91613, CombatPackets.ActorState.NpcLeaveCombat, trailing: 3)));
    }

    [Fact]
    public async Task Sweep_ClosesASurvivedEngagement_State5ThenState1()
    {
        var data = await Data.Value;
        await using var rig = await Rig.StartAsync(data);
        rig.AddPlayer();
        var npc = rig.AddMonster();
        await rig.Chat.EnterCombatAsync(npc, 0x0D790911);    

        var started = DateTime.UtcNow;
        await rig.SweepAsync();
        var elapsed = DateTime.UtcNow - started;
        var states = rig.States(await rig.FramesUntilMarkerAsync());

        Assert.Equal(new[]
        {
            Hex(CombatPackets.BuildChangeState2398(npc.EntityOid, 3, targetHandle: 0x0D790911, trailing: 3, combatFlag: 1)),
            Hex(CombatPackets.BuildChangeState2398(npc.EntityOid, CombatPackets.ActorState.NpcReturn, trailing: 3)),
            Hex(CombatPackets.BuildChangeState2398(npc.EntityOid, CombatPackets.ActorState.NpcLeaveCombat, trailing: 3)),
        }, states);
        Assert.True(elapsed >= NpcChatService.ReturnToIdleDelay - TimeSpan.FromMilliseconds(20), elapsed.ToString());

        
        await rig.Chat.EnterCombatAsync(npc, 0x0D790911);
        Assert.Single(rig.States(await rig.FramesUntilMarkerAsync()));
    }

    [Fact]
    public async Task Sweep_ReEngagedDuringTheReturn_SendsNoState1()
    {
        var data = await Data.Value;
        await using var rig = await Rig.StartAsync(data);
        rig.AddPlayer();
        var npc = rig.AddMonster();
        await rig.Chat.EnterCombatAsync(npc, 0x0D790911);

        var pass = rig.SweepAsync();
        await rig.WaitForStateAsync(CombatPackets.ActorState.NpcEnterCombat);
        await rig.WaitForStateAsync(CombatPackets.ActorState.NpcReturn);
        await rig.Chat.EnterCombatAsync(npc, 0x0D790911);    
        await pass;

        var states = rig.States(await rig.FramesUntilMarkerAsync()).Select(b => Convert.FromHexString(b)[14]).ToArray();
        Assert.Equal(new byte[] { CombatPackets.ActorState.NpcEnterCombat }, states);
    }

    [Fact]
    public async Task Sweep_KilledDuringTheReturn_SendsNoState1()
    {
        var data = await Data.Value;
        await using var rig = await Rig.StartAsync(data);
        rig.AddPlayer();
        var npc = rig.AddMonster();
        await rig.Chat.EnterCombatAsync(npc, 0x0D790911);

        var pass = rig.SweepAsync();
        await rig.WaitForStateAsync(CombatPackets.ActorState.NpcEnterCombat);
        await rig.WaitForStateAsync(CombatPackets.ActorState.NpcReturn);
        rig.Chat.OnNpcLeftCombat(npc.EntityOid);              
        await pass;

        Assert.Empty(rig.States(await rig.FramesUntilMarkerAsync()));
    }

    [Fact]
    public async Task Sweep_DeadMonster_GetsNoState5()
    {
        var data = await Data.Value;
        await using var rig = await Rig.StartAsync(data);
        rig.AddPlayer();
        var npc = rig.AddMonster();
        await rig.Chat.EnterCombatAsync(npc, 0x0D790911);
        npc.Hp = 0;
        npc.IsDead = true;

        await rig.SweepAsync();
        var states = rig.States(await rig.FramesUntilMarkerAsync()).Select(b => Convert.FromHexString(b)[14]).ToArray();
        Assert.Equal(new byte[] { CombatPackets.ActorState.NpcEnterCombat }, states);
    }

    
 /// <summary>The 2472 and 2488 burst is sent twice right after an offline-kick 2570, in the same millisecond.</summary>
    private const string Retail2472 =
        "12020000138c9ca7b3b6e00d1a0000000200f3510900030000000000000000000000000102002d5109000500000000000000000000000002"
        + "0200a17809000500000000000000000000000003020011770900050000000000000000000000000504000200000000000000000000000000"
        + "000000060200ad8e5b0001000000000000000000000000070300490000000000000000000000000000000008020089d61e00010000000000"
        + "000000000000010002008e5109000200000000000000000000000101020002790900020000000000000000000000010202006150090001"
        + "000000000000000000000001030200cd79090005000000000000000000000001040200a976090001000000000000000000000001070300"
        + "270000000000000000000000000000000108030010000000000000000000000000000000010903001700000000000000000000000000000"
        + "002000200ad8e5b000100000000000000000000000201020071770900010000000000000000000000020202004976090005000000000000"
        + "000000000002030200015009000500000000000000000000000204020039780900010000000000000000000000020501002fd4fe0001000"
        + "00075718b03a73d040e020603002c000000000000000000000000000000020703001e000000000000000000000000000000020803001c00"
        + "0000000000000000000000000000020903002e000000000000000000000000000000";

    private const string Retail2488 =
        "0284008400b3e3000000001e000000354f09009d4f09000150090061500900c55009002d5109008e510900f35109001d53090049760900"
        + "a97609001177090071770900d777090039780900a178090002790900cd79090001093d0067093d00cd093d00950a3d00fb0a3d005f0b3d00"
        + "c30b3d00270c3d008e0c3d00f20c3d00560d3d00ba0d3d00";

    [Fact]
    public void KickBurst_ReproducesRetail_2472_2488_Twice()
    {
        var bar2472 = Convert.FromHexString(Retail2472);
        var uid = BinaryPrimitives.ReadUInt64LittleEndian(bar2472.AsSpan(4));
        int n = BinaryPrimitives.ReadUInt16LittleEndian(bar2472.AsSpan(12));
        var bar = new List<CharacterQuickSlot>();
        for (int i = 0, o = 14; i < n; i++, o += 20)
            bar.Add(new CharacterQuickSlot
            {
                Page = bar2472[o], Slot = bar2472[o + 1], EntryType = bar2472[o + 2],
                EntryId = BinaryPrimitives.ReadUInt32LittleEndian(bar2472.AsSpan(o + 4)),
                Amount = checked((ushort)BinaryPrimitives.ReadUInt32LittleEndian(bar2472.AsSpan(o + 8))),
                ItemUid = BinaryPrimitives.ReadUInt64LittleEndian(bar2472.AsSpan(o + 12)),
            });

        var list2488 = Convert.FromHexString(Retail2488);
        var blob = list2488.AsSpan(3);                                       
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob[8..]);
        var skills = new List<(uint Group, byte Level)>();
        for (int i = 0; i < count; i++)
        {
            var id = BinaryPrimitives.ReadUInt32LittleEndian(blob[(12 + 4 * i)..]);
            skills.Add((id / 100, (byte)(id % 100)));
        }

        var burst = GuildSkillBarRefresh.BuildBurst(uid, bar, skills);
        Assert.Equal(new[]
        {
            ZoneS2COpcodes.OnQuickSlotSaveData, ZoneS2COpcodes.OnUseSkillList,
            ZoneS2COpcodes.OnQuickSlotSaveData, ZoneS2COpcodes.OnUseSkillList,
        }, burst.Select(f => f.Opcode).ToArray());
        Assert.Equal(Retail2472, Hex(burst[0].Body));
        Assert.Equal(Retail2488, Hex(burst[1].Body));
        Assert.Equal(Retail2472, Hex(burst[2].Body));
        Assert.Equal(Retail2488, Hex(burst[3].Body));
    }

    
    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
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
            if (File.Exists(Path.Combine(candidate, "Npc_Dlg.json")) && File.Exists(Path.Combine(candidate, "Position_Info.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/Npc_Dlg.json");
    }

 /// <summary>
 /// A real <see cref="ProudSession"/> on a loopback <see cref="ProudServer"/>, read from the client end, with a PC
 /// on map 1100 and the NpcChatService the sweep runs in.
 /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly ProudServer _server;
        private readonly Socket _client;

        public ProudSession Session { get; }
        public PlayerStateManager Players { get; }
        public NpcStateManager Npcs { get; }
        public NpcChatService Chat { get; }

        private Rig(ProudServer server, Socket client, ProudSession session, GameDataStore data)
        {
            _server = server;
            _client = client;
            Session = session;
            Players = new PlayerStateManager(new NoDirtySink());
            Npcs = new NpcStateManager(data, NullLogger<NpcStateManager>.Instance);
            var broadcast = new BroadcastService(Players, NullLogger<BroadcastService>.Instance);
            broadcast.SetServer(server);
            Chat = new NpcChatService(data, Npcs, Players, broadcast, NullLogger<NpcChatService>.Instance);
        }

        public static async Task<Rig> StartAsync(GameDataStore data)
        {
            var server = new ProudServer("test", NullLogger.Instance);
            var connected = new TaskCompletionSource<ProudSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.SessionConnected += s => { connected.TrySetResult(s); return Task.CompletedTask; };
            await server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync(server.LocalEndPoint!);
            var session = await connected.Task.WaitAsync(Generous);
            return new Rig(server, client, session, data);
        }

 /// <summary>An in-world PC on map 1100, far from the monster (no idle chatter), not fighting.</summary>
        public PlayerState AddPlayer()
        {
            var player = new PlayerState
            {
                SessionId = Session.SessionId, EntityId = 0x0D790911, CharacterId = 915_611, Name = "Sweeper", Level = 30,
                MapId = 1100, MapLoaded = true, Hp = 500, MaxHp = 500, Mp = 300, MaxMp = 300, Character = Hero(),
            };
            Players.AddPlayer(Session.SessionId, player);
            return player;
        }

 /// <summary>A live monster on the PC's map with no threat target.</summary>
        public NpcRuntimeState AddMonster()
        {
            var spawn = new SpawnEntry { MapId = 1100, Index = 9_101, AssetIndex = 0, PosX = 500f, PosY = 500f };
            Npcs.TrackSpawns(1100, [spawn]);
            var npc = Npcs.GetAliveNpcs(1100).Single();
            npc.MaxHp = 100;
            npc.Hp = 100;
            return npc;
        }

 /// <summary>One NpcChatService pass (its private TickPassAsync, which Tick runs), awaited by the caller.</summary>
        public Task SweepAsync()
        {
            var pass = typeof(NpcChatService).GetMethod("TickPassAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            return (Task)pass.Invoke(Chat, null)!;
        }

 /// <summary>The 2398 bodies among <paramref name="frames"/>, in order.</summary>
        public List<string> States(IEnumerable<(ushort Op, string Body)> frames)
            => frames.Where(f => f.Op == ZoneS2COpcodes.OnChangeState).Select(f => f.Body).ToList();

 /// <summary>Reads frames until a 2398 with <paramref name="state"/> (blob+12, body byte 14) arrives.</summary>
        public async Task WaitForStateAsync(byte state)
        {
            while (true)
            {
                var frame = await NextRmiHexAsync();
                if (frame.Op == ZoneS2COpcodes.OnChangeState && Convert.FromHexString(frame.Body)[14] == state)
                    return;
            }
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
            _client.Dispose();
            await _server.StopAsync();
        }
    }
}
