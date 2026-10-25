using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Tests for the 1531 departure removals, the per-player 1561 repeat window, the guild-exit refresh sent on a 1432 leave, and the cooldown refusal codes of a 1508 cast (2715 and 2962).
/// </summary>
public class Reaudit2_A4_Tests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);
    private static int _nextChar = 1_970_000_000;

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
    private static byte[] FromHex(string s) => Convert.FromHexString(s);
    private static (ushort, string) Hexed((ushort Op, byte[] Body) frame) => (frame.Op, Hex(frame.Body));

    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }

    
 /// <summary>
 /// The member's 1531 departure removes the Warp_Handling 0 effect from its PC, sending 2493 and 2494 before the hand-off. PartyDungeonTeleportHandler calls WarpDeparture.DropWarpEffectsAsync ahead of DungeonChannelHandOff.EnterAsync to do this.
 /// </summary>
    [Fact]
    public async Task DungeonTeleport_DropsWarpHandling0Effects_WithRetailsRemovalFrames()
    {
        var store = await LoadStoreAsync("Effect_Table.json");
        Assert.True(WarpDeparture.IsDroppedOnWarp(store, 60037));
        Assert.Equal("01141400c8e30000000085ea00000000000000000000", Hex(EffectPackets.BuildDelEffectSuccess2493(60037)));
        Assert.Equal("01181800c9e30000000085ea000011ec20000000000000000000",
            Hex(ActorVitalsPackets.BuildDelEffect2494(60037, 0x0020EC11)));
    }

    
 /// <summary>
 /// The repeat window is per player: after an answered 9000211, a 1561 of another item skill within 1,000 ms is a repeat too. A refusal does not move the window.
 /// </summary>
    [Fact]
    public void ItemSkillRepeat_IsOneWindowPerPlayer_AcrossItemSkills()
    {
        var player = new PlayerState();
        var t0 = new DateTime(2026, 7, 3, 1, 0, 0, DateTimeKind.Utc);
        Assert.False(ItemSkillRepeatGuard.IsRepeat(player, t0));
        ItemSkillRepeatGuard.MarkAnswered(player, t0);
        Assert.True(ItemSkillRepeatGuard.IsRepeat(player, t0.AddMilliseconds(10)));    
        Assert.True(ItemSkillRepeatGuard.IsRepeat(player, t0.AddMilliseconds(999)));   
        Assert.False(ItemSkillRepeatGuard.IsRepeat(player, t0.AddMilliseconds(1000)));
        Assert.False(ItemSkillRepeatGuard.IsRepeat(new PlayerState(), t0.AddMilliseconds(10)));
    }

 /// <summary>
 /// Through the handler: an answered 1561 opens the window, and a 1561 of a different item skill inside it gets the lone 2711 before the item is looked up. The 2711 has the shape 01 10 1000 49e4 00000000 [caster] [skill] (see the SkillStartItemHandler remarks).
 /// </summary>
    [Fact]
    public async Task ItemSkillRepeat_OfAnotherSkill_GetsTheLone2711()
    {
        await using var net = await Harness.StartAsync(1);
        var players = new PlayerStateManager(new NoDirtySink());
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(net.Server);
        var sid = net.Sessions[0].SessionId;
        var caster = new PlayerState { SessionId = sid, EntityId = 0x0AC7DE11, MapId = 1100, MapLoaded = true };
        players.AddPlayer(sid, caster);
        await using var services = new ServiceCollection().BuildServiceProvider();
        var handler = new SkillStartItemHandler(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SkillStartItemHandler>.Instance, players, broadcast);

        ItemSkillRepeatGuard.MarkAnswered(caster, DateTime.UtcNow);   
        var request = new byte[55];
        BinaryPrimitives.WriteUInt32LittleEndian(request, 9110001);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(16), 0x0AC7DE11);
        await handler.HandleAsync(net.Sessions[0], request);

        Assert.Equal((ZoneS2COpcodes.NfySkillCastingCancel, "0110100049e40000000011dec70af1018b00"),
            Hexed(await net.Readers[0].NextAsync()));
    }

    
    
    
    private const string LeaveBar7448b82e =
        "5a0000008c67f4a7b3b6e00d0400000003001127000000000000000000000000000000010200f54106000100000000000000000000000002" +
        "0200bd42060001000000000000000000000000030200b1440600010000000000000000000000";
    private const string LeaveSkills7448b82e = "01181800b3e30000000003000000f5410600bd420600b1440600";

    
    
    private const string LeaveBarD414c84e =
        "4e02000081d9e7a7b3b6e00d1d00000002007990060001000000000000000000000000010200f5410600010000000000000000000000000202" +
        "005942060001000000000000000000000000030200ce9406000a000000000000000000000000040200c94906000500000000000000000000" +
        "00000502003d93060009000000d3dcd007a83d040e0006020041b01e000100000000000000000000000007020025a41e0001000000000000" +
        "00000000000008020089d61e00010000001b9d0201a83d040e000902004ea106000a000000000000000000000001000200ac8e1e00040000" +
        "00000000000000000001010200add01e00010000000000000000000000010202002b9506000300000000000000000000000103020065940600" +
        "05000000000000000000000001040200d88f1e000400000035e89c0ca83d040e0105020070981e0004000000000000000000000001060300" +
        "1127000000000000000000000000000001070200b38f0600030000001b9d0201a83d040e01080200594b06000500000048635b03a83d040e" +
        "01090200e190060005000000000000000000000002000200b3440600030000000000000000000000020102001a9006000600000000000000" +
        "0000000002020200e9ad1e0001000000abbccf07a83d040e02040200f6ab1e0002000000000000000000000002050200d5985b0001000000" +
        "4dc94b0ca83d040e02060100338793030600000006628509a83d040e02070200fd991e000100000000000000000000000208010059dbfe00" +
        "01000000cc615e06a83d040e020901005b384201ff000000ecf0080ca83d040e";
    private const string LeaveSkillsD414c84e =
        "01545400b3e30000000012000000f541060059420600bd420600ea430600b3440600c9490600594b0600b38f06001a900600799006" +
        "00e1900600d29206003d93060065940600ce9406002b9506004ea1060083e74c00";

 /// <summary>A 2472 read back into the bar it describes.</summary>
    private static (ulong Uid, List<CharacterQuickSlot> Bar) ParseBar(byte[] body)
    {
        var uid = BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(4));
        int n = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(12));
        var bar = new List<CharacterQuickSlot>(n);
        for (var i = 0; i < n; i++)
        {
            var r = body.AsSpan(14 + 20 * i, 20);
            Assert.Equal(0, r[3]);
            bar.Add(new CharacterQuickSlot
            {
                Page = r[0], Slot = r[1], EntryType = r[2],
                EntryId = BinaryPrimitives.ReadUInt32LittleEndian(r[4..]),
                Amount = checked((ushort)BinaryPrimitives.ReadUInt32LittleEndian(r[8..])),
                ItemUid = BinaryPrimitives.ReadUInt64LittleEndian(r[12..]),
            });
        }
        return (uid, bar);
    }

 /// <summary>The (group, level) pairs of a 2488: count at blob+8, ids group*100+level from blob+12.</summary>
    private static Dictionary<uint, byte> ParseSkills(byte[] body)
    {
        Assert.Equal(1, body[0]);
        var blob = body.AsSpan(2);
        int n = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(blob[8..]));
        var skills = new Dictionary<uint, byte>();
        for (var i = 0; i < n; i++)
        {
            var id = BinaryPrimitives.ReadUInt32LittleEndian(blob[(12 + 4 * i)..]);
            skills[id / 100] = (byte)(id % 100);
        }
        return skills;
    }

    [Theory]
    [InlineData(LeaveBar7448b82e, LeaveSkills7448b82e)]
    [InlineData(LeaveBarD414c84e, LeaveSkillsD414c84e)]
    public void GuildLeave_Burst_IsTheArchives_2472_2488_2472_2488(string barHex, string skillsHex)
    {
        var (uid, bar) = ParseBar(FromHex(barHex));
        var skills = ParseSkills(FromHex(skillsHex)).Select(kv => (kv.Key, kv.Value)).ToArray();

        var burst = GuildSkillBarRefresh.BuildBurst(uid, bar, skills);

        Assert.Equal(new[] { ZoneS2COpcodes.OnQuickSlotSaveData, ZoneS2COpcodes.OnUseSkillList,
                             ZoneS2COpcodes.OnQuickSlotSaveData, ZoneS2COpcodes.OnUseSkillList },
                     burst.Select(b => b.Opcode).ToArray());
        Assert.Equal(barHex, Hex(burst[0].Body));
        Assert.Equal(skillsHex, Hex(burst[1].Body));
        Assert.Equal(barHex, Hex(burst[2].Body));
        Assert.Equal(skillsHex, Hex(burst[3].Body));
    }

    
    
    private const string Lines2723HeavensGate =
        "00004a000000120029d5010055d60100b9d601001dd7010099fc0100a90d0300bb3403001f35030083350300e735030047360300c95b0300" +
        "2d5c0300905c0300f95c03005e5d0300c25d0300265e0300";

 /// <summary>
 /// The 2488 of a guild leave is built after RemoveMemberByCharIdAsync, since the burst is sent only on result 0, past the removal. The guild's link skills are in the member's list while it is a member and gone afterwards. The guild runs on GuildService.Instance without a database: InitAsync is never called, so the write-through is skipped.
 /// </summary>
    [Fact]
    public async Task GuildLeave_2488_IsTheGuildlessList_OnceTheMemberIsRemoved()
    {
        var store = await LoadStoreAsync("Guild_Skill_Level.json");
        var lines = FromHex(Lines2723HeavensGate);
        int n = BinaryPrimitives.ReadUInt16LittleEndian(lines.AsSpan(6));
        var guildSkills = Enumerable.Range(0, n)
            .Select(i => BinaryPrimitives.ReadUInt32LittleEndian(lines.AsSpan(8 + 4 * i))).ToList();
        Assert.Equal(18, guildSkills.Count);

        var master = (uint)Interlocked.Increment(ref _nextChar);
        var member = (uint)Interlocked.Increment(ref _nextChar);
        var player = new PlayerState { CharacterId = member, LearnedSkills = ParseSkills(FromHex(LeaveSkills7448b82e)) };
        var (guild, refusal) = await GuildService.Instance.CreateAsync(master, "Master", 60, 6, $"Leave {Guid.NewGuid():N}");
        Assert.Equal(GuildService.CreateRefusal.None, refusal);
        var g = Assert.IsType<Guild>(guild);
        try
        {
            lock (g.Gate) g.Skills.AddRange(guildSkills);
            await GuildService.Instance.AddMemberAsync(g, member, "Leaver", 40, 3);
            Assert.NotEqual(LeaveSkills7448b82e,
                Hex(WorldEntryService.BuildUseSkillList2488(GuildSkillBookLearn.SkillListFor(player, store))));

            Assert.NotNull(await GuildService.Instance.RemoveMemberByCharIdAsync(g, member));
            var (uid, bar) = ParseBar(FromHex(LeaveBar7448b82e));
            var burst = GuildSkillBarRefresh.BuildBurst(uid, bar, GuildSkillBookLearn.SkillListFor(player, store));
            Assert.Equal(LeaveBar7448b82e, Hex(burst[0].Body));
            Assert.Equal(LeaveSkills7448b82e, Hex(burst[1].Body));
            Assert.Equal(LeaveSkills7448b82e, Hex(burst[3].Body));
        }
        finally { await GuildService.Instance.DisbandAsync(g); }
    }

    
    
    
    
    private const string Request412105 =
        "c9490600a6f72d4471ef8243506b3e42130d0301015a985909017cf302000000000000000000000000000000000100";
    private const string Refusal20032 = "404ec9490600130d030101000000000000000000000000000000000100";
    private const string Error2962_20032 = "120082e400000000404ec949060011520f07";

    private static async Task<(Harness Net, SkillStartHandler Handler, PlayerState Caster, ServiceProvider Services)>
        StartSkillHandlerAsync()
    {
        var net = await Harness.StartAsync(1);
        var players = new PlayerStateManager(new NoDirtySink());
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(net.Server);
        var sid = net.Sessions[0].SessionId;
        var caster = new PlayerState { SessionId = sid, EntityId = 0x070F5211, MapId = 1100, MapLoaded = true,
            Level = 1, Hp = 500, Mp = 500 };
        players.AddPlayer(sid, caster);
        var collection = new ServiceCollection();
        collection.AddSingleton(await LoadStoreAsync("Skill_Table_SkillLevel.json"));
        var services = collection.BuildServiceProvider();
        var handler = new SkillStartHandler(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SkillStartHandler>.Instance, players, broadcast);
        return (net, handler, caster, services);
    }

 /// <summary>The idle caster of a refused cast gets the 20032 code in both the 2715 and the 2962.</summary>
    [Fact]
    public async Task CooldownRefusal_OfAnIdleCaster_IsRetails20032()
    {
        var (net, handler, caster, services) = await StartSkillHandlerAsync();
        await using (net)
        await using (services)
        {
            Assert.Equal(TimeSpan.FromSeconds(1), SkillCasterBusyWindow.CastDuration(431310));
            var now = DateTime.UtcNow;
            caster.SkillCooldowns[4121] = now.AddSeconds(8);
            SkillCasterBusyWindow.Record(caster, now.AddMilliseconds(-1039), SkillCasterBusyWindow.CastDuration(431310));

            await handler.HandleAsync(net.Sessions[0], FromHex(Request412105));
            Assert.Equal((ZoneS2COpcodes.AnsUseSkillStart, Refusal20032), Hexed(await net.Readers[0].NextAsync()));
            Assert.Equal((ZoneS2COpcodes.NfySkillStartError, Error2962_20032), Hexed(await net.Readers[0].NextAsync()));
        }
    }

 /// <summary>
 /// Inside a cast the refusal is the silent 20152: the accepted 1508 of the previous cast (1 s long) records the busy window, and a cooldown-blocked 1508 inside that window is refused with 20152 (0x4EB8) in both the 2715 and the 2962.
 /// </summary>
 /// <remarks>
 /// No assertion depends on wall-clock time. The handler stamps the record with its own DateTime.UtcNow, so the test brackets that stamp between before and after, and checks the window at times derived from them: busy at before and at before + 1,000 ms, idle at after + 1,001 ms. This pins a window of exactly 1 s at a start inside the bracket. The refusal half re-records the caster as inside a cast that cannot run out during the test (<see cref="Generous"/>). A slow host that stalls more than 1 s between the accepted 1508 and the next one lets the window lapse, which turns the expected 20152 into 20032.
 /// </remarks>
    [Fact]
    public async Task CooldownRefusal_InsideTheLastAcceptedCast_Stays20152()
    {
        var (net, handler, caster, services) = await StartSkillHandlerAsync();
        await using (net)
        await using (services)
        {
            caster.SkillCooldowns[4121] = DateTime.UtcNow.Add(Generous);
            var accepted = FromHex(Request412105);
            BinaryPrimitives.WriteUInt32LittleEndian(accepted, 431310);
            var before = DateTime.UtcNow;
            await handler.HandleAsync(net.Sessions[0], accepted);
            var after = DateTime.UtcNow;
            var accept = await net.Readers[0].NextAsync();
            Assert.Equal(ZoneS2COpcodes.AnsUseSkillStart, accept.Op);
            Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(accept.Body));
            Assert.Equal(ZoneS2COpcodes.NfyUseSkillStart, (await net.Readers[0].NextAsync()).Op);
            Assert.Equal(TimeSpan.FromSeconds(1), SkillCasterBusyWindow.CastDuration(431310));
            Assert.False(SkillCasterBusyWindow.IsIdle(caster, before));
            Assert.False(SkillCasterBusyWindow.IsIdle(caster, before.AddMilliseconds(1000)));
            Assert.True(SkillCasterBusyWindow.IsIdle(caster, after.AddMilliseconds(1001)));

            SkillCasterBusyWindow.Record(caster, DateTime.UtcNow, Generous);
            await handler.HandleAsync(net.Sessions[0], FromHex(Request412105));
            Assert.Equal((ZoneS2COpcodes.AnsUseSkillStart, "b84e" + Refusal20032[4..]), Hexed(await net.Readers[0].NextAsync()));
            Assert.Equal((ZoneS2COpcodes.NfySkillStartError, Error2962_20032.Replace("404e", "b84e")),
                Hexed(await net.Readers[0].NextAsync()));
        }
    }

 /// <summary>The window: idle with no record, busy until the duration has passed, idle strictly after it.</summary>
    [Fact]
    public void CasterBusyWindow_IsIdleOnlyPastTheLastCastsDuration()
    {
        var player = new PlayerState();
        var t0 = new DateTime(2026, 7, 21, 15, 55, 8, 915, DateTimeKind.Utc);
        Assert.True(SkillCasterBusyWindow.IsIdle(player, t0));
        Assert.Equal(SkillCasterBusyWindow.SkillResultNotEnoughTime, SkillCasterBusyWindow.RefusalCode(player, t0));
        SkillCasterBusyWindow.Record(player, t0, TimeSpan.FromSeconds(1));
        Assert.False(SkillCasterBusyWindow.IsIdle(player, t0.AddMilliseconds(1000)));
        Assert.Equal(SkillPackets.SkillResultOnCooldown, SkillCasterBusyWindow.RefusalCode(player, t0.AddMilliseconds(500)));
        Assert.True(SkillCasterBusyWindow.IsIdle(player, t0.AddMilliseconds(1039)));
        Assert.Equal((ushort)20032, SkillCasterBusyWindow.SkillResultNotEnoughTime);
    }

    
    private static async Task<GameDataStore> LoadStoreAsync(params string[] tables)
    {
        var source = DataDir();
        var dir = Path.Combine(Path.GetTempPath(), "ro2-reaudit2-a4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new GameDataStore();
        try
        {
            foreach (var t in tables) File.Copy(Path.Combine(source, t), Path.Combine(dir, t));
            await store.LoadAsync(dir);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
        return store;
    }

 /// <summary>The repo's data/ folder, found by walking up from the test binaries (any machine, and CI).</summary>
    private static string DataDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "Skill_Table_SkillLevel.json"))) return candidate;
        }
        throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/Skill_Table_SkillLevel.json");
    }

 /// <summary>A loopback ProudServer with N connected clients, each socket known to be its session's.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        public required ProudServer Server { get; init; }
        public required List<Socket> Clients { get; init; }
        public required List<ProudSession> Sessions { get; init; }
        public required List<FrameReader> Readers { get; init; }

        public static async Task<Harness> StartAsync(int count)
        {
            var server = new ProudServer("test", NullLogger.Instance);
            var next = new TaskCompletionSource<ProudSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.SessionConnected += s => { Volatile.Read(ref next).TrySetResult(s); return Task.CompletedTask; };
            await server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
            var clients = new List<Socket>();
            var sessions = new List<ProudSession>();
            for (var i = 0; i < count; i++)
            {
                var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await client.ConnectAsync(server.LocalEndPoint!);
                sessions.Add(await next.Task.WaitAsync(Generous));
                clients.Add(client);
                Volatile.Write(ref next, new TaskCompletionSource<ProudSession>(TaskCreationOptions.RunContinuationsAsynchronously));
            }
            return new Harness { Server = server, Clients = clients, Sessions = sessions,
                Readers = clients.Select(c => new FrameReader(c)).ToList() };
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var c in Clients) c.Dispose();
            await Server.StopAsync();
        }
    }

 /// <summary>Reads the internal RMI frames (<c>[13 57][w][len][01][u16 op][body]</c>) a test client receives.</summary>
    private sealed class FrameReader(Socket socket)
    {
        public async Task<(ushort Op, byte[] Body)> NextAsync()
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
                int n = await socket.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None).AsTask().WaitAsync(Generous);
                if (n == 0) throw new EndOfStreamException("connection closed mid-frame");
                read += n;
            }
            return buffer;
        }
    }
}
