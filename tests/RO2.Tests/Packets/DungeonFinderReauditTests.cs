using System.Globalization;
using RO2.Domain.GameData;
using RO2.Domain.GameData.Tables;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Tests the dungeon finder join (1559) and the instance reset (1534) as one system: the entry-count gate
/// and its refusal (2556 19015 and 2810 [03]), the 2824 message, the channel hand-off into the list's
/// dungeon, the entry count taken at the join, the instance a 1534 resets with 2771, and a cleared instance
/// that closes after <see cref="DungeonInstanceRegistry.ClearedInstanceLifetime"/>. The entry count is kept
/// in a local standing in for DungeonEntryCountRepository within one daily window. Tables come from data/.
/// </summary>
public class DungeonFinderReauditTests
{
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
        public void At(string utc) => Now = DateTimeOffset.Parse(utc + "Z", CultureInfo.InvariantCulture);
    }

 /// <summary>One character's finder runs into DungeonID 63 (list 21) on a registry with a manual clock.</summary>
    private sealed class FinderReplay
    {
        private readonly DungeonInfo _row;
        private readonly uint _arrivalMap;
        private readonly uint _character;

        public FinderReplay(DungeonInfo row, uint arrivalMap, uint character, string startUtc)
        {
            _row = row;
            _arrivalMap = arrivalMap;
            _character = character;
            Clock = new ManualClock(DateTimeOffset.Parse(startUtc + "Z", CultureInfo.InvariantCulture));
            Registry = new DungeonInstanceRegistry(Clock);
            Owner = DungeonInstanceOwner.For(null, character);
        }

        public ManualClock Clock { get; }
        public DungeonInstanceRegistry Registry { get; }
        public DungeonInstanceOwner Owner { get; }
        public int Used { get; private set; }

 /// <summary>3035 for dungeon 63 as the server answers it.</summary>
        public string Read3035() => Hex(DungeonEntranceInfoHandler.Build3035(63, (ushort)Used, _row.Enter_Count));

 /// <summary>The 1559 handler's path: the entry-count gate on every join, the finder entry, and the count
 /// after the hand-off. Returns the 19015 refusal, or 0 and the entry.</summary>
        public (ushort Refusal, DungeonInstanceEntry? Entry) Join(string utc)
        {
            Clock.At(utc);
            var refusal = DungeonEntryGate.CheckEnterCount(_row, wouldCount: true, Used);
            if (refusal != 0) return (refusal, null);
            var entry = Registry.EnterFinder(Owner, _row.DungeonID, _arrivalMap, _character);
            if (entry.CountsForCharacter) Used++;
            return (0, entry);
        }

 /// <summary>The boss kill: DungeonFinderPendingStore.TryEndRun's MarkCleared.</summary>
        public void Kill(string utc, DungeonInstanceEntry entry)
        {
            Clock.At(utc);
            Registry.MarkCleared(entry.InstanceId);
        }

        public string[] Reset(string utc, uint fromMap)
        {
            Clock.At(utc);
            return Registry.Reset(Owner, fromMap, _character).Select(id => Hex(DungeonResetHandler.Build2771(id))).ToArray();
        }
    }

    private static async Task<FinderReplay> ReplayAsync(uint character, string startUtc)
    {
        var data = await FinderDataAsync();
        var list = Assert.Single(data.FindDungeonList, r => r.List_ID == 21);
        var row = Assert.Single(data.DungeonInfo, d => d.DungeonID == list.Dungeon_ID);
        Assert.Equal(63, row.DungeonID);
        Assert.Equal(3, row.Enter_Count);
        var arrival = data.Positions[row.Position_ID];
        Assert.Equal(410104u, arrival.Area_ID);                                 
        Assert.Equal(DungeonEntryGate.InstanceMapType, data.GetMap(arrival.Area_ID)!.Map_Type);
        return new FinderReplay(row, arrival.Area_ID, character, startUtc);
    }

 /// <summary>
 /// A run that the old count-at-reset rule got wrong: a 3035 reads 0, then a join, the boss, the exit, and the
 /// next 3035 reads 1 with no 1534 or 2771 in between. A second join made while the first run's instance is
 /// still open also counts, so the following 3035 reads 2.
 /// </summary>
    [Fact]
    public async Task Replay_9896fe5b_14f214ee_TheJoinCounts_WithNoReset()
    {
        var r = await ReplayAsync(0xA7FA6E78u, "2026-08-05T12:14:13.641");
        Assert.Equal("3f00000000000300", r.Read3035());                        
        var (refusal1, run1) = r.Join("2026-08-05T12:14:15.867");             
        Assert.Equal(0, refusal1);
        r.Kill("2026-08-05T12:19:29.809", run1!.Value);                       
        Assert.Equal("3f00000001000300", r.Read3035());                        
        var (refusal2, run2) = r.Join("2026-08-05T12:23:03.538");             
        Assert.Equal(0, refusal2);
        Assert.True(run2 is { IsNewInstance: true, CountsForCharacter: true }); 
        Assert.False(r.Registry.IsOpen(run1.Value.InstanceId));
        
        Assert.Equal(new[] { "3f000000" }, r.Reset("2026-08-05T12:25:24.882", 1100)); 
        Assert.Empty(r.Reset("2026-08-05T12:25:26.968", 1100));
        Assert.Empty(r.Reset("2026-08-05T12:55:41.688", 1100));
        Assert.Equal("3f00000002000300", r.Read3035());                        
    }

 /// <summary>
 /// A reset 94 seconds after the kill is answered, and the 1534s sent after the cleared lifetime are silent.
 /// The run counted all the same.
 /// </summary>
    [Fact]
    public async Task Replay_d414c84e_0a3de40a_ACleared_Instance_ClosesOnItsOwn()
    {
        var r = await ReplayAsync(0xA7E8421Cu, "2026-07-31T20:07:35.591");
        Assert.Equal("3f00000000000300", r.Read3035());                        
        var (_, run1) = r.Join("2026-07-31T20:07:35.992");
        r.Kill("2026-07-31T20:10:09.012", run1!.Value);                       
        Assert.Equal(new[] { "3f000000" }, r.Reset("2026-07-31T20:11:43.426", 1100));
        Assert.Empty(r.Reset("2026-07-31T20:11:43.824", 1100));
        Assert.Equal("3f00000001000300", r.Read3035());                        
        var (_, run2) = r.Join("2026-07-31T20:11:50.199");
        r.Kill("2026-07-31T20:14:17.899", run2!.Value);                       
        Assert.Empty(r.Reset("2026-07-31T20:25:01.785", 1100));               
        Assert.False(r.Registry.IsOpen(run2.Value.InstanceId));
        Assert.Empty(r.Reset("2026-07-31T20:25:53.693", 1100));
        Assert.Equal("3f00000002000300", r.Read3035());                        
        var (refusal3, _) = r.Join("2026-07-31T20:25:59.144");                
        Assert.Equal(0, refusal3);
    }

 /// <summary>
 /// Three runs, each reset right after; the 3035 steps 0, 1, 2, 3, and the fourth join is refused with the
 /// lone 2556 19015.
 /// </summary>
    [Fact]
    public async Task Replay_d414c84e_19a9bbaf_ThreeRuns_ThenTheLimit()
    {
        var r = await ReplayAsync(0xA7EA3299u, "2026-08-03T19:41:37.550");
        (string Join, string Kill, string Reset, string Read)[] runs =
        [
            ("2026-08-03T19:41:37.550", "2026-08-03T19:43:52.362", "2026-08-03T19:44:28.350", "3f00000001000300"),
            ("2026-08-03T19:44:47.285", "2026-08-03T19:47:00.616", "2026-08-03T19:47:15.982", "3f00000002000300"),
            ("2026-08-03T19:47:31.645", "2026-08-03T19:49:32.022", "2026-08-03T19:50:58.530", "3f00000003000300"),
        ];
        Assert.Equal("3f00000000000300", r.Read3035());
        foreach (var (join, kill, reset, read) in runs)
        {
            var (refusal, entry) = r.Join(join);
            Assert.Equal(0, refusal);
            r.Kill(kill, entry!.Value);
            Assert.Equal(new[] { "3f000000" }, r.Reset(reset, 1100));
            Assert.Equal(read, r.Read3035());
        }
        var (fourth, none) = r.Join("2026-08-03T19:51:06.314");
        Assert.Equal(DungeonEntryGate.ResultEnterCountExceeded, fourth);
        Assert.Null(none);
    }

 /// <summary>
 /// Every finder, reset and 3035 frame of one session. The 1534 sent 924 seconds after the first run's kill
 /// is silent, as expected. This server counts the first run, which the reference does not, so the 3035
 /// values read one above the reference (0 and 1). This is the known divergence of the join rule
 /// (see the DungeonInstanceRegistry remarks). It does not reach a 19015 in this sequence.
 /// Every 2771 the reference sent is reproduced.
 /// </summary>
    [Fact]
    public async Task Replay_d414c84e_052fb38a_EveryFinderAndResetFrame()
    {
        var r = await ReplayAsync(0xA7ECF196u, "2026-07-18T01:03:40.426");
        Assert.Equal("3f00000000000300", r.Read3035());                        
        var (_, run1) = r.Join("2026-07-18T01:03:41.251");                    
        r.Kill("2026-07-18T01:12:46.797", run1!.Value);                       
        Assert.Empty(r.Reset("2026-07-18T01:28:10.892", 1100));               
        Assert.Equal("3f00000001000300", r.Read3035());                        
        var (_, run2) = r.Join("2026-07-18T01:40:26.937");
        Assert.True(run2 is { IsNewInstance: true, CountsForCharacter: true });
        r.Kill("2026-07-18T02:51:55.993", run2!.Value);                       
        Assert.Equal(new[] { "3f000000" }, r.Reset("2026-07-18T02:52:34.324", 1100)); 
        foreach (var t in new[] { "03:00:49.241", "03:00:49.651", "03:00:50.059", "03:00:50.467", "03:01:29.341",
                     "03:01:29.750", "03:01:30.160" })
            Assert.Empty(r.Reset("2026-07-18T" + t, 1100));                    
        Assert.Equal("3f00000002000300", r.Read3035());                        
        var (refusal3, run3) = r.Join("2026-07-18T03:01:58.534");             
        Assert.Equal(0, refusal3);
        r.Kill("2026-07-18T03:06:38.634", run3!.Value);                       

        
        
        var data = await FinderDataAsync();
        var dungeon8 = Assert.Single(data.DungeonInfo, d => d.DungeonID == 8);
        Assert.Equal(412103u, data.Positions[dungeon8.Position_ID].Area_ID);
        r.Clock.At("2026-07-18T03:08:49.402");
        r.Registry.Enter(r.Owner, 8, 412103, 0xA7ECF196u);
        Assert.Equal(new[] { "08000000", "3f000000" }, r.Reset("2026-07-18T03:11:11.335", 1111));
        Assert.Empty(r.Reset("2026-07-18T03:11:11.335", 1111));
    }

 /// <summary>
 /// A 3035 reading 3 at 09:26:16. The 1559s that follow are each refused with the lone 2556 19015 and no 2824,
 /// and the 2810s that follow carry 03.
 /// </summary>
    [Fact]
    public async Task UsedUpFinderJoin_IsRefusedWith19015_And2810Reason3()
    {
        var data = await FinderDataAsync();
        var row = Assert.Single(data.DungeonInfo, d => d.DungeonID == 63);

        Assert.Equal("3f00000003000300", Hex(DungeonEntranceInfoHandler.Build3035(63, 3, row.Enter_Count)));
        var code = DungeonEntryGate.CheckEnterCount(row, wouldCount: true, 3);
        Assert.Equal(DungeonEntryGate.ResultEnterCountExceeded, code);
        Assert.Equal(19015, code);
        Assert.Equal("1e212e00474a" + new string('0', 75 * 2), Hex(DungeonEntryGate.BuildRefusal2556(0x002E211Eu, code)));
        Assert.Equal("03", Hex(DungeonFinderPackets.BuildJoinFail2810(DungeonFinderPackets.JoinFailMoveFailed)));
        
        foreach (var below in new[] { 0, 1, 2 })
            Assert.Equal(0, DungeonEntryGate.CheckEnterCount(row, true, below));
    }

    [Fact]
    public void ClearedLifetime_ClosesOnlyClearedInstances_AtTenMinutes()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 7, 31, 20, 0, 0, TimeSpan.Zero));
        var registry = new DungeonInstanceRegistry(clock);
        var owner = DungeonInstanceOwner.For(null, 0x5A1F_0010u);
        var finder = registry.EnterFinder(owner, 63, 410104, 0x5A1F_0010u);
        var level = registry.Enter(owner, 64, 426101, 0x5A1F_0010u);   
        registry.MarkCleared(finder.InstanceId);
        Assert.Equal(TimeSpan.FromMinutes(10), DungeonInstanceRegistry.ClearedInstanceLifetime);

        clock.At("2026-07-31T20:09:59.999");
        Assert.True(registry.IsOpen(finder.InstanceId));
        clock.At("2026-07-31T20:10:00");
        Assert.False(registry.IsOpen(finder.InstanceId));
        Assert.True(registry.IsOpen(level.InstanceId));
        Assert.False(registry.WouldCount(owner, 64, 0x5A1F_0010u));
        Assert.Equal(new byte[] { 64 }, registry.Reset(owner, 1221, 0x5A1F_0010u));      

        
        var again = registry.EnterFinder(owner, 63, 410104, 0x5A1F_0010u);
        var other = registry.Enter(owner, 64, 426101, 0x5A1F_0010u);
        registry.MarkCleared(again.InstanceId);
        clock.At("2026-07-31T20:30:00");
        Assert.Empty(registry.Reset(owner, 410104, 0x5A1F_0010u));
        Assert.True(registry.IsOpen(other.InstanceId));
    }

 /// <summary>
 /// The finder's 2824 body: <c>[u32 List_ID][u8 healers][u8 dealers][u8 tanks]</c>, for example
 /// <c>15000000 000100</c> (list 21) or <c>16000000 000100</c> (list 22).
 /// </summary>
    [Theory]
    [InlineData(21u, "15000000000100")]
    [InlineData(22u, "16000000000100")]
    public void JoinMsg2824_IsRetails(uint listId, string retail)
        => Assert.Equal(retail, Hex(DungeonFinderPackets.BuildInProgressJoinMsg2824(listId,
            new DungeonFinderQueue.RoleCounts(Tanks: 0, Healers: 0, Dealers: 1))));

 /// <summary>
 /// The finder join's hand-off 2556: account, ticket, host and port, then the destination block taken from
 /// Position_Info for the dungeon's position point (map, position, direction) and the character GID.
 /// </summary>
    [Fact]
    public async Task FinderHandOff2556_IsRetails()
    {
        const string retail = "ad274d00" + "0000"                                 
            + "5006d5ac7ae4a44bb3908cc3bef99c7f"                                  
            + "3100320038002e003200340031002e00390033002e0032003100310000000000"  
            + "2c1c"                                                              
            + "f8410600" + "000090420000014300001042" + "87"                      
            + "96f1eca7b3b6e00d";                                                 
        var data = await FinderDataAsync();
        var list = Assert.Single(data.FindDungeonList, r => r.List_ID == 21);
        var row = Assert.Single(data.DungeonInfo, d => d.DungeonID == list.Dungeon_ID);
        var arrival = data.Positions[row.Position_ID];

        var body = DungeonChannelHandOff.BuildAnsChannelMove2556(0x004D27ADu,
            Convert.FromHexString("5006d5ac7ae4a44bb3908cc3bef99c7f"), "128.241.93.211", 7212, arrival,
            0x0DE0B6B3A7ECF196UL);

        Assert.Equal(DungeonChannelHandOff.AnsChannelMoveBytes * 2, retail.Length);
        Assert.Equal(retail, Hex(body));
    }

 /// <summary>
 /// The finder run outlives the hand-off: a reconnect creates a new PlayerState for the same character, and
 /// the boss kill must still find the run. A 1534 that resets the run's instance ends it.
 /// </summary>
    [Fact]
    public void FinderRun_IsKeptPerCharacter_AndEndsWithItsInstance()
    {
        const uint character = 0x5A1F_0001u;
        var owner = DungeonInstanceOwner.For(null, character);
        var entry = DungeonInstanceRegistry.Shared.EnterFinder(owner, 63, 410104, character);
        var run = new DungeonFinderPendingStore.FinderRun(21, 410104, entry.InstanceId);
        DungeonFinderPendingStore.SetRun(new PlayerState { CharacterId = character }, run);

        var afterReconnect = new PlayerState { CharacterId = character };
        Assert.Same(run, DungeonFinderPendingStore.GetRun(afterReconnect));
        Assert.Null(DungeonFinderPendingStore.GetRun(new PlayerState { CharacterId = character + 1 }));

        Assert.Equal(new byte[] { 63 }, DungeonInstanceRegistry.Shared.Reset(owner, 1100, character));
        Assert.Null(DungeonFinderPendingStore.GetRun(afterReconnect));
        Assert.False(DungeonFinderPendingStore.TryEndRun(afterReconnect, run));
    }

 /// <summary>A later finder join replaces the run's instance, so the old run is over.</summary>
    [Fact]
    public void FinderRun_EndsWhenANewJoinReplacesItsInstance()
    {
        const uint character = 0x5A1F_0004u;
        var owner = DungeonInstanceOwner.For(null, character);
        var first = DungeonInstanceRegistry.Shared.EnterFinder(owner, 63, 410104, character);
        var run = new DungeonFinderPendingStore.FinderRun(21, 410104, first.InstanceId);
        var player = new PlayerState { CharacterId = character };
        DungeonFinderPendingStore.SetRun(player, run);

        DungeonInstanceRegistry.Shared.EnterFinder(owner, 63, 410104, character);
        Assert.Null(DungeonFinderPendingStore.GetRun(player));
        Assert.Equal(new byte[] { 63 }, DungeonInstanceRegistry.Shared.Reset(owner, 1100, character));
    }

 /// <summary>The boss kill ends the run once and starts the instance's cleared lifetime.</summary>
    [Fact]
    public void FinderRun_IsPaidOnce_AndItsInstanceStaysOpenAfterTheKill()
    {
        const uint character = 0x5A1F_0002u;
        var owner = DungeonInstanceOwner.For(null, character);
        var entry = DungeonInstanceRegistry.Shared.EnterFinder(owner, 63, 410104, character);
        var player = new PlayerState { CharacterId = character };
        var run = new DungeonFinderPendingStore.FinderRun(21, 410104, entry.InstanceId);
        DungeonFinderPendingStore.SetRun(player, run);

        Assert.True(DungeonFinderPendingStore.TryEndRun(new PlayerState { CharacterId = character }, run));
        Assert.False(DungeonFinderPendingStore.TryEndRun(player, run));
        Assert.Null(DungeonFinderPendingStore.GetRun(player));
        Assert.True(DungeonInstanceRegistry.Shared.IsOpen(entry.InstanceId));
        Assert.Equal(new byte[] { 63 }, DungeonInstanceRegistry.Shared.Reset(owner, 1100, character));

        var noInstance = new DungeonFinderPendingStore.FinderRun(21, 410104, null);
        DungeonFinderPendingStore.SetRun(player, noInstance);
        Assert.True(DungeonFinderPendingStore.TryEndRun(player, noInstance));
    }

 /// <summary>The pending match belongs to the connection's PlayerState: a new one (relog) has none.</summary>
    [Fact]
    public void PendingMatch_IsPerPlayerState()
    {
        var player = new PlayerState { CharacterId = 0x5A1F_0003u };
        var match = new DungeonFinderPendingStore.PendingMatch(21, 2, new DungeonFinderQueue.RoleCounts(0, 0, 1));
        DungeonFinderPendingStore.SetPending(player, match);

        Assert.Null(DungeonFinderPendingStore.TakePending(new PlayerState { CharacterId = player.CharacterId }));
        Assert.Same(match, DungeonFinderPendingStore.TakePending(player));
        Assert.Null(DungeonFinderPendingStore.TakePending(player));
    }

 /// <summary>The tables the finder join reads, copied from data/ into a private directory.</summary>
    private static async Task<GameDataStore> FinderDataAsync()
    {
        var source = RequireDataDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "ro2-reaudit-a1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var table in new[] { "DungeonInfo.json", "Position_Info.json", "Map_List.json", "Find_Dungeon_List.json" })
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

    private static string RequireDataDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "Find_Dungeon_List.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/Find_Dungeon_List.json");
    }
}
