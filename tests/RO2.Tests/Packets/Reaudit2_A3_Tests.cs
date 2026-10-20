using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Domain.GameData.Tables;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Tests.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Second re-audit, unit A3: the 2577 of a mail send / claim (1393 / 1396), who receives a 2630 (the players whose
/// open threat meter, their latest 1466, is that monster), the repeated 2903 of a lapsed Helper, and the PC record's
/// move speed (rec+57 = the speed of the PC's 2497). Hex strings are retail frames. Shipped tables are found by
/// walking up from the test output directory.
/// </summary>
/// <remarks>In the MountService-state collection: the rec+57 test mounts a PC, and MountService is process-wide and
/// keyed by session id (see RidingServiceTests).</remarks>
[Collection(MountStateCollection.Name)]
public class Reaudit2_A3_Tests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);
    private static readonly Lazy<KharaCatalog> Catalog = new(() => new KharaCatalog(LoadKharaRows()));
    private static readonly Lazy<Task<GameDataStore>> Data = new(LoadDataAsync);

    // One shared instance (CA1869).
    private static readonly JsonSerializerOptions DataJsonOptions =
        new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    // ---- 2577 on a mail send (1393) and a mail claim (1396) ------------------------------------------------------

    /// <summary>
    /// 1393 ReqMailSend, then 2577
    /// <c>3a02000001c50e16020000</c> (Khara 570, row 1 = held item 35000005, count 0) and the send's 2445. The
    /// attachment left the bag, so the held-count hook MailSendHandler now calls reports the new count; this pins
    /// that the engine turns "35000005 is no longer held" into the retail bytes.
    /// </summary>
    [Fact]
    public void MailSend_AttachmentLeavingTheBag_IsRetails2577()
    {
        var state = new KharaStateDocument { Id = 1, Active = new List<KharaActiveDocument> { Active(570, 0, 1) } };
        var engine = Engine(state, held: new Dictionary<uint, int> { [35000005] = 0 });

        engine.OnHeldCountChanged(35000005);

        Assert.Equal(new[] { "2577:3a02000001c50e16020000" }, Frames(engine));
    }

    /// <summary>
    /// 1396 ReqMailGetItem, then 2577
    /// <c>5602000000b26f54010100</c> (Khara 598 row 0, item 22310834, count 1), 2575 598 (<c>56020000</c>), 2576 0, ...,
    /// 2575 593 (<c>51020000</c>) and 2575 588, all before the claim's 2447. The claimed item landing is the held-count
    /// change MailGetItemHandler now reports; 598 completing then completes the Complete_Type 40 chain 593 -> 588.
    /// </summary>
    [Fact]
    public void MailClaim_ItemLanding_Is2577ThenTheCompletionChain()
    {
        var state = new KharaStateDocument
        {
            Id = 1,
            Active = new List<KharaActiveDocument> { Active(598, 0), Active(593, 1, 1, 1, 0), Active(588, 1, 0) },
            Completed = new List<uint> { 582, 587, 592, 594 },
        };
        var engine = Engine(state, held: new Dictionary<uint, int> { [22310834] = 1 });

        engine.OnHeldCountChanged(22310834);

        var frames = Frames(engine);
        Assert.Equal("2577:5602000000b26f54010100", frames[0]);
        Assert.Equal("2575:56020000", frames[1]);
        var i593 = Array.IndexOf(frames, "2575:51020000");
        var i588 = Array.IndexOf(frames, "2575:4c020000");
        Assert.True(i593 > 1 && i588 > i593, string.Join(" ", frames));
        Assert.Contains(598u, engine.State.Completed);
        Assert.Contains(593u, engine.State.Completed);
        Assert.Contains(588u, engine.State.Completed);
    }

    // ---- 2630 NfyAggroChanged: the players whose open meter (latest 1466) is the monster ----------------------------

    /// <summary>
    /// The audience rule: a receiver whose latest monster-tagged 1466 ReqOpenAggroList named that monster gets the
    /// 2630, while a damaging PC hit alone does not give a 2630 to a receiver that had not opened the meter. So a
    /// watcher on the map is told whoever hit, and the attacker and its party are told only if they opened that
    /// meter. A watcher on another map is not (same-map bound), and no monster (handle 0) has no audience.
    /// </summary>
    [Fact]
    public void AggroRecipients_AreThePlayersWhoseOpenMeterIsTheMonster()
    {
        const uint monster = 0x01636313, other = 0x01636213;
        var attacker = Pc(1, 0x00000111, 1300);      // hit the monster, never opened its meter
        var party = Pc(2, 0x00000211, 1300);         // attacker's party, meter on another monster
        var onlooker = Pc(3, 0x00000311, 1300);      // no party, meter on this monster
        var away = Pc(4, 0x00000411, 1100);          // meter on this monster, another map
        var meters = new Dictionary<PlayerState, uint> { [party] = other, [onlooker] = monster, [away] = monster };
        uint MeterOf(PlayerState p) => meters.GetValueOrDefault(p);
        var all = new[] { attacker, party, onlooker, away };

        Assert.Equal(new uint[] { 3 },
            NpcCombatService.AggroRecipients(monster, 1300, all, MeterOf).Select(p => p.SessionId).ToArray());
        Assert.Equal(new uint[] { 2 },
            NpcCombatService.AggroRecipients(other, 1300, all, MeterOf).Select(p => p.SessionId).ToArray());
        Assert.Empty(NpcCombatService.AggroRecipients(0, 1300, all, MeterOf));
    }

    /// <summary>
    /// End to end through NpcCombatService's flush: X hits a monster (bytes unchanged:
    /// <c>[npc][target][01 01][01][pc][02 01 n name][threat]</c>, AggroPacketsTests has the retail frame). The
    /// onlooker Z, outside any party, opened the monster's meter and gets the step; W opened it and then sent a
    /// PC-tagged 1466, which is ignored, so W gets it too. X
    /// never opened the meter and Y, X's party member, opened another monster's: neither gets anything before a
    /// marker sent once Z and W have theirs.
    /// </summary>
    [Fact]
    public async Task AggroFlush_ReachesTheOpenMeters_NotTheAttackerOrItsParty()
    {
        await using var net = await Harness.StartAsync(4);
        var players = new PlayerStateManager(new NoDirtySink());
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(net.Server);
        var parties = new PartyService(players, broadcast, NullLogger<PartyService>.Instance);
        var x = Pc(net.Sessions[0].SessionId, 0x070F4011, 1300, "Carl3ee");
        var y = Pc(net.Sessions[1].SessionId, 0x070F4111, 1300);
        var z = Pc(net.Sessions[2].SessionId, 0x070F4211, 1300);
        var w = Pc(net.Sessions[3].SessionId, 0x070F4311, 1300);
        foreach (var p in new[] { x, y, z, w }) players.AddPlayer(p.SessionId, p);
        var state = new PartyState { PartyId = 9, LeaderSessionId = x.SessionId, FounderGid = x.CharacterUid,
            MemberSessionIds = [x.SessionId, y.SessionId] };
        state.Members[x.SessionId] = x; state.Members[y.SessionId] = y;
        parties.Track(state);

        var data = new GameDataStore();
        var npcs = new NpcStateManager(data, NullLogger<NpcStateManager>.Instance);
        var spawn = new SpawnEntry { MapId = 1300, Index = 383, PosX = 447.5f, PosY = 321.1f, PosZ = 12.5f, AssetIndex = 28580000 };
        var spawn2 = new SpawnEntry { MapId = 1300, Index = 384, PosX = 450.5f, PosY = 321.1f, PosZ = 12.5f, AssetIndex = 28580000 };
        npcs.TrackSpawns(spawn.MapId, [spawn, spawn2]);
        var handle = npcs.AllocateHandle(spawn.MapId, spawn.Index);
        var otherHandle = npcs.AllocateHandle(spawn2.MapId, spawn2.Index);
        var npc = npcs.GetNpc(handle)!;
        npc.Camp = 2;
        npc.MaxHp = 100;
        npc.Hp = 100;

        var collection = new ServiceCollection();
        collection.AddSingleton(parties);
        await using var services = collection.BuildServiceProvider();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        var combat = new NpcCombatService(npcs, players, broadcast,
            new DurabilityService(data, NullLogger<DurabilityService>.Instance, scopes, broadcast),
            new NpcChatService(data, npcs, players, broadcast, NullLogger<NpcChatService>.Instance),
            new PcCombatStateService(players, broadcast, NullLogger<PcCombatStateService>.Instance),
            scopes, NullLogger<NpcCombatService>.Instance);

        combat.WatchAggroMeter(y, otherHandle);
        combat.WatchAggroMeter(z, otherHandle);
        combat.WatchAggroMeter(z, handle);          // a later 1466 moves the meter
        combat.WatchAggroMeter(w, handle);
        combat.WatchAggroMeter(w, x.EntityId);      // PC-tagged: ignored, W keeps the monster
        Assert.Equal(handle, combat.AggroMeterOf(z));
        Assert.Equal(handle, combat.AggroMeterOf(w));
        Assert.Equal(0u, combat.AggroMeterOf(x));

        npcs.ApplyDamage(handle, 45, x.EntityId, x.Name);
        combat.Tick(1.0);

        var expected = Hex(AggroPackets.BuildAggroChanged2630(handle, x.EntityId, x.EntityId, x.Name, 45));
        Assert.Equal((ZoneS2COpcodes.NfyAggroChanged, expected), Hexed(await net.Readers[2].NextAsync()));
        Assert.Equal((ZoneS2COpcodes.NfyAggroChanged, expected), Hexed(await net.Readers[3].NextAsync()));
        // X and Y get no AGGRO step (2630): X never opened the meter, Y opened another monster's. The monster's
        // reset heals it to full and broadcasts that HP to the whole map as an NPC 2401 OnActorAutoHeal, which the
        // attacker X does receive (it is the floating-bar refresh), so the stream is filtered to the aggro frame.
        Assert.DoesNotContain(await UntilMarkerAsync(net, 0), f => f.Op == ZoneS2COpcodes.NfyAggroChanged);
        Assert.DoesNotContain(await UntilMarkerAsync(net, 1), f => f.Op == ZoneS2COpcodes.NfyAggroChanged);
    }

    // ---- 2903 NfyHelperNotice: repeated while the lapsed Helper stays above the member ---------------------------

    /// <summary>The founder's lapsed Helper flag, repeated 7 times byte-identical.</summary>
    private const string Retail2903 = "4c4feba7b3b6e00d00";

    /// <summary>
    /// The member's level-up to 41 lapses the level-50 founder's flag (2903 + 2361); each later
    /// level-up below 50 ends with the same 2903 to the member only, and no roster, and the
    /// level-up to 50 sends none. The Helper gets no 2903 on a repeat.
    /// </summary>
    [Fact]
    public async Task LapsedHelper_IsRepeatedAtEachLevelUpBelowIt_AndNotAtItsLevel()
    {
        await using var net = await Harness.StartAsync(2);
        var players = new PlayerStateManager(new NoDirtySink());
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(net.Server);
        var party = new PartyService(players, broadcast, NullLogger<PartyService>.Instance);
        var helper = new PlayerState { SessionId = net.Sessions[0].SessionId, EntityId = 0x0B000011, CharacterUid = 0x0DE0B6B3A7EB4F4CUL,
            Name = "Founder", Level = 50, MapId = 1100 };
        var member = new PlayerState { SessionId = net.Sessions[1].SessionId, EntityId = 0x0B000111, CharacterUid = 0x0DE0B6B3A7F4678CUL,
            Name = "Member", Level = 39, MapId = 1100 };
        players.AddPlayer(helper.SessionId, helper);
        players.AddPlayer(member.SessionId, member);
        var state = new PartyState { PartyId = 0x40, LeaderSessionId = helper.SessionId, FounderGid = helper.CharacterUid,
            MemberSessionIds = [helper.SessionId, member.SessionId] };
        state.Members[helper.SessionId] = helper;
        state.Members[member.SessionId] = member;
        state.FrameSlots[helper.SessionId] = 1;
        state.FrameSlots[member.SessionId] = 2;
        party.Track(state);
        state.HelperFlags[helper.CharacterUid] = HelperPackets.FlagDeclared;

        var collection = new ServiceCollection();
        collection.AddSingleton(party);
        await using var services = collection.BuildServiceProvider();
        var data = new GameDataStore();   // no exp table: every level needs 999,999
        var combat = new CombatService(players, new NpcStateManager(data, NullLogger<NpcStateManager>.Instance), broadcast,
            data, NullLogger<CombatService>.Instance, services.GetRequiredService<IServiceScopeFactory>());

        // 39 -> 41: the revoke (2903, then the roster).
        await combat.GrantExpAsync(member, 2 * 999_999UL);
        var first = await UntilMarkerAsync(net, 1);
        Assert.Equal(new[] { ZoneS2COpcodes.NfyHelperNotice, ZoneS2COpcodes.OnPartyNotifyUpdate },
            first.Where(f => f.Op is ZoneS2COpcodes.NfyHelperNotice or ZoneS2COpcodes.OnPartyNotifyUpdate).Select(f => f.Op).ToArray());
        Assert.Contains(helper.CharacterUid, state.LapsedHelperGids.Keys);
        await UntilMarkerAsync(net, 0);

        // 41 -> 43: the repeat, the LAST frame of the burst, no roster; nothing of it to the Helper.
        await combat.GrantExpAsync(member, 2 * 999_999UL);
        Assert.Equal(43, member.Level);
        var repeat = await UntilMarkerAsync(net, 1);
        Assert.Equal((ZoneS2COpcodes.NfyHelperNotice, Retail2903), repeat[^1]);
        Assert.Single(repeat, f => f.Op == ZoneS2COpcodes.NfyHelperNotice);
        Assert.DoesNotContain(repeat, f => f.Op == ZoneS2COpcodes.OnPartyNotifyUpdate);
        var helperSaw = await UntilMarkerAsync(net, 0);
        Assert.DoesNotContain(helperSaw, f => f.Op is ZoneS2COpcodes.NfyHelperNotice or ZoneS2COpcodes.OnPartyNotifyUpdate);

        // 43 -> 50: the Helper is no longer above the member; no 2903 (retail's level-50 burst has none).
        await combat.GrantExpAsync(member, 7 * 999_999UL);
        Assert.Equal(50, member.Level);
        Assert.DoesNotContain(await UntilMarkerAsync(net, 1), f => f.Op == ZoneS2COpcodes.NfyHelperNotice);
    }

    /// <summary>The repeat test on its own: helper 50 against member 41..49 true, 50 false; a Helper still 10 or more
    /// above is not lapsed (it meets the client's rule, HelperPackets.CanBeHelper).</summary>
    [Fact]
    public void LapsedHelperStillAbove_IsTheClientRuleSeenFromTheMember()
    {
        for (byte m = 41; m <= 49; m++)
            Assert.True(PartyService.LapsedHelperStillAbove(50, m));
        Assert.False(PartyService.LapsedHelperStillAbove(50, 50));
        Assert.False(PartyService.LapsedHelperStillAbove(50, 40));   // 50 >= 40 + 10: still a valid Helper
    }

    /// <summary>A new declaration (or quit) ends the lapsed state, so the repeat stops.</summary>
    [Fact]
    public async Task Redeclaring_ClearsTheLapsedGid()
    {
        var players = new PlayerStateManager(new NoDirtySink());
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        var party = new PartyService(players, broadcast, NullLogger<PartyService>.Instance);
        var helper = new PlayerState { SessionId = 1, EntityId = 0x0111, CharacterUid = 0x0DE0B6B3A7EB4F4CUL, Name = "Founder", Level = 50, MapId = 1100 };
        var member = new PlayerState { SessionId = 2, EntityId = 0x0211, CharacterUid = 0x0DE0B6B3A7F4678CUL, Name = "Member", Level = 45, MapId = 1100 };
        players.AddPlayer(1, helper);
        players.AddPlayer(2, member);
        var state = new PartyState { PartyId = 0x41, LeaderSessionId = 1, FounderGid = helper.CharacterUid, MemberSessionIds = [1, 2] };
        state.Members[1] = helper; state.Members[2] = member;
        party.Track(state);
        state.LapsedHelperGids[helper.CharacterUid] = 0;

        await party.SetHelperFlagAsync(helper, HelperPackets.FlagQuit);

        Assert.Empty(state.LapsedHelperGids);
    }

    // ---- 2315 / 2504 record rec+57 = the PC's 2497 speed -----------------------------------------------------------

    /// <summary>
    /// A 2315's rec+57 (blob+69) equals the session's latest 2497 speed for the same handle. A PC riding 90020000
    /// (RidingInfo Speed 1.4, ×1.0) is sent 2497 7.0 (the mount RidingServiceTests pins), so its record now carries
    /// 7.0 too; on foot it is the modal 6.0 again.
    /// </summary>
    [Fact]
    public async Task PcRecordSpeed_IsTheSpeedOfThePcs2497()
    {
        var data = await Data.Value;
        await using var net = await Harness.StartAsync(14);   // a session id no other mount test's rig uses
        var session = net.Sessions[^1];
        MountService.Dismount(session.SessionId);
        var players = new PlayerStateManager(new NoDirtySink());
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(net.Server);
        var collection = new ServiceCollection();
        collection.AddSingleton(data);
        collection.AddSingleton(broadcast);
        collection.AddSingleton(sp => new EffectService(players, broadcast, data, npcState: null!, loot: null!,
            sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<EffectService>.Instance, emblems: TestEmblems.Create()));
        await using var services = collection.BuildServiceProvider();
        var player = new PlayerState
        {
            SessionId = session.SessionId, EntityId = 0x0B9F5B11u, CharacterId = 915_101, CharacterUid = 0x0DE0B6B3A7000001UL,
            Name = "Rider", Level = 30, MapId = 1100, MapLoaded = true, Hp = 1000, MaxHp = 1000, Mp = 100, MaxMp = 100,
        };
        var character = new Character { Id = 915_101, Name = "Rider", Job = 1, Level = 30, Gender = 1, Race = 1 };
        player.Character = character;
        players.AddPlayer(session.SessionId, player);

        try
        {
            Assert.Equal(6.0f, RecordSpeed(ActorStatePackets.BuildOnPCViewData2315(player, character, null, TestEmblems.Create())));

            await RidingService.MountAsync(session, player, vehicleId: 90020000, skillId: 6000301, services);
            var told = ActorMoveSpeed.CurrentSpeedOf(session, player, data);
            Assert.Equal(7.0f, told);
            Assert.Equal(told, ActorMoveSpeed.CurrentSpeedOf(player, data));
            Assert.Equal(told, RecordSpeed(ActorStatePackets.BuildOnPCViewData2315(player, character, null, TestEmblems.Create())));
            // No store: only the foot speed can be stated.
            Assert.Equal(TraitPackets.PcModalWorldEntrySpeed, ActorMoveSpeed.CurrentSpeedOf(player, null));
        }
        finally
        {
            MountService.Dismount(session.SessionId);
        }
        Assert.Equal(6.0f, RecordSpeed(ActorStatePackets.BuildOnPCViewData2315(player, character, null, TestEmblems.Create())));
    }

    /// <summary>rec+57 of a 2315 body: <c>[u8 n][len][blob]</c>, blob = <c>[u16 selfLen][u16 58007][u32 0][u32 handle]
    /// [record]</c>, so rec+57 = blob+69.</summary>
    private static float RecordSpeed(byte[] body)
    {
        var blob = body.AsSpan(1 + body[0]);
        return BinaryPrimitives.ReadSingleLittleEndian(blob[69..]);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private static PlayerState Pc(uint session, uint handle, uint map, string? name = null) => new()
    {
        SessionId = session, EntityId = handle, CharacterUid = 0x0DE0B6B3A7000000UL + session,
        Name = name ?? "Pc" + session, Level = 30, MapId = map, MapLoaded = true, Hp = 100, MaxHp = 100,
    };

    private static async Task<List<(ushort Op, string Body)>> UntilMarkerAsync(Harness net, int index)
    {
        await net.Sessions[index].SendInternalRmiAsync(0, new byte[] { 0x7E, 0x57 });
        var frames = new List<(ushort Op, string Body)>();
        while (true)
        {
            var frame = Hexed(await net.Readers[index].NextAsync());
            if (frame == (0, "7e57")) return frames;
            frames.Add(frame);
        }
    }

    private static (ushort, string) Hexed((ushort Op, byte[] Body) frame) => (frame.Op, Hex(frame.Body));

    private static KharaEngine Engine(KharaStateDocument state, Dictionary<uint, int> held) =>
        new(Catalog.Value, state, new KharaFacts { Level = 50, HeldCount = id => held.GetValueOrDefault(id) });

    private static KharaActiveDocument Active(uint kharaId, params uint[] counts)
    {
        var rows = Catalog.Value.Get(kharaId)!.Conditions.Count;
        var list = new List<uint>(counts);
        while (list.Count < rows) list.Add(0);
        return new KharaActiveDocument { KharaId = kharaId, State = KharaService.StateInProgress, Counts = list };
    }

    private static string[] Frames(KharaEngine engine) =>
        engine.Frames.Select(f => $"{f.Opcode}:{Hex(f.Body)}").ToArray();

    private static string RequireDataDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "Khara_Info.json")) && File.Exists(Path.Combine(candidate, "RidingInfo.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/Khara_Info.json");
    }

    private static List<KharaInfo> LoadKharaRows() =>
        JsonSerializer.Deserialize<List<KharaInfo>>(
            File.ReadAllText(Path.Combine(RequireDataDirectory(), "Khara_Info.json")), DataJsonOptions)!;

    private static async Task<GameDataStore> LoadDataAsync()
    {
        var store = new GameDataStore();
        await store.LoadAsync(RequireDataDirectory());
        return store;
    }

    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
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
