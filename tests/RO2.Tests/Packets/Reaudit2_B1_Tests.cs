using System.Buffers.Binary;
using System.Text.Json;
using RO2.Domain.GameData.Tables;
using RO2.Network.Packets;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Tests for dungeon reset on the empty-instance clock (DungeonInstanceRegistry.RecordLoadedMap and ForgetLoadedMap, for 1534 and 2771), and for the Khara titles on the play clock (KharaTitleClock) with their title blocks (KharaTitleStore.TitleOf), for 1438, 2582 and 2592.
/// </summary>
/// <remarks>
/// The clock tests feed in the instants of the reference frames. Where the server's own instant matters, such as when it resumes the clock at login, the test says which one it takes. The dungeon tests pin the reset rule on recorded elapsed times, and the DungeonIDs they use are stand-ins: 2771 carries only the DungeonID.
/// </remarks>
public class Reaudit2_B1_Tests
{
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    private static byte[] Bytes(string h) => Convert.FromHexString(h);

    private static bool Any(uint _) => true;

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(double seconds) => Now += TimeSpan.FromMilliseconds(Math.Round(seconds * 1000));
    }

    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }

    private const uint Prontera = 1100;
    private const uint InstanceMap = 426101; 

    
    [Fact]
    public void EmptyInstance_IsAnsweredAt185s_AndSilentPastTenMinutes()
    {
        
        
        const uint character = 0x5B1D_0001u;
        var owner = DungeonInstanceOwner.For(null, character);

        var clock = new ManualClock(new DateTimeOffset(2026, 7, 18, 1, 0, 0, TimeSpan.Zero));
        var registry = new DungeonInstanceRegistry(clock);
        registry.Enter(owner, 64, InstanceMap, character);
        clock.Advance(3);
        registry.RecordLoadedMap(character, InstanceMap);          
        clock.Advance(300);
        Assert.Equal(InstanceMap, registry.RecordLoadedMap(character, Prontera)); 
        clock.Advance(185.7);
        Assert.Equal(new[] { "40000000" }, registry.Reset(owner, Prontera, character).Select(id => Hex(DungeonResetHandler.Build2771(id))));

        clock = new ManualClock(new DateTimeOffset(2026, 7, 18, 2, 0, 0, TimeSpan.Zero));
        registry = new DungeonInstanceRegistry(clock);
        var entry = registry.Enter(owner, 64, InstanceMap, character);
        registry.RecordLoadedMap(character, InstanceMap);
        registry.RecordLoadedMap(character, Prontera);
        Assert.Equal(clock.Now, registry.VacantSince(entry.InstanceId));
        clock.Advance(599.999);
        Assert.True(registry.IsOpen(entry.InstanceId));
        clock.Advance(0.001);
        Assert.Equal(TimeSpan.FromMinutes(10), DungeonInstanceRegistry.EmptyInstanceLifetime);
        Assert.False(registry.IsOpen(entry.InstanceId));
        clock.Advance(239.8);                                       
        Assert.Empty(registry.Reset(owner, Prontera, character));
        
        Assert.True(registry.Enter(owner, 64, InstanceMap, character) is { IsNewInstance: true, CountsForCharacter: true });
    }

    [Fact]
    public void UnvisitedOrOccupiedInstance_HasNoEmptyClock()
    {
        
        var clock = new ManualClock(new DateTimeOffset(2026, 7, 18, 3, 0, 0, TimeSpan.Zero));
        var registry = new DungeonInstanceRegistry(clock);
        const uint a = 0x5B1D_0011u, b = 0x5B1D_0012u;
        var party = DungeonInstanceOwner.For(new PartyState { PartyId = 0x5B1D_0010u }, a);
        var entry = registry.Enter(party, 64, InstanceMap, a);
        registry.Enter(party, 64, InstanceMap, b);
        Assert.Null(registry.VacantSince(entry.InstanceId));

        registry.RecordLoadedMap(a, InstanceMap);
        registry.RecordLoadedMap(b, InstanceMap);
        registry.RecordLoadedMap(a, Prontera);                      
        clock.Advance(1800);
        Assert.True(registry.IsOpen(entry.InstanceId));
        Assert.Null(registry.VacantSince(entry.InstanceId));
        Assert.Equal(InstanceMap, registry.ForgetLoadedMap(b));     
        Assert.Equal(clock.Now, registry.VacantSince(entry.InstanceId));
        clock.Advance(600);
        Assert.Empty(registry.Reset(party, Prontera, a));
        Assert.Null(registry.ForgetLoadedMap(b));                   
    }

    [Fact]
    public void ReturnOrReEntry_StopsTheEmptyClock()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 7, 18, 4, 0, 0, TimeSpan.Zero));
        var registry = new DungeonInstanceRegistry(clock);
        const uint character = 0x5B1D_0021u;
        var owner = DungeonInstanceOwner.For(null, character);
        var entry = registry.Enter(owner, 64, InstanceMap, character);
        registry.RecordLoadedMap(character, InstanceMap);
        registry.RecordLoadedMap(character, Prontera);
        clock.Advance(540);
        Assert.Equal(Prontera, registry.RecordLoadedMap(character, InstanceMap)); 
        Assert.Null(registry.VacantSince(entry.InstanceId));
        registry.RecordLoadedMap(character, Prontera);
        clock.Advance(540);
        var again = registry.Enter(owner, 64, InstanceMap, character); 
        Assert.Equal(new DungeonInstanceEntry(false, false, entry.InstanceId), again);
        Assert.Null(registry.VacantSince(entry.InstanceId));
        clock.Advance(1200);                                         
        Assert.Equal(new byte[] { 64 }, registry.Reset(owner, Prontera, character));
    }

    [Fact]
    public void LeavingTheWorld_StartsTheClock_UnlessAnotherSessionHoldsTheCharacter()
    {
        
        
        const uint character = 0x5B1D_0031u;
        var owner = DungeonInstanceOwner.For(null, character);
        var registry = DungeonInstanceRegistry.Shared;
        var entry = registry.Enter(owner, 64, InstanceMap, character);
        try
        {
            registry.RecordLoadedMap(character, InstanceMap);
            KharaTitleClock.Resume(character, DateTime.UtcNow);
            var players = new PlayerStateManager(new NoDirtySink());
            players.AddPlayer(1, new PlayerState { SessionId = 1, CharacterId = character, MapId = InstanceMap });
            players.AddPlayer(2, new PlayerState { SessionId = 2, CharacterId = character, MapId = InstanceMap });

            Assert.True(players.RemovePlayer(1, out _));
            Assert.Null(registry.VacantSince(entry.InstanceId));
            Assert.True(KharaTitleClock.IsRunning(character));

            Assert.True(players.RemovePlayer(2, out _));
            Assert.NotNull(registry.VacantSince(entry.InstanceId));
            Assert.False(KharaTitleClock.IsRunning(character));
            Assert.Null(registry.ForgetLoadedMap(character));
        }
        finally
        {
            registry.Reset(owner, Prontera, character);
            KharaTitleClock.Forget(character);
        }
    }

    
    private static DateTime At(int day, int h, int m, int s, int ms) => new(2026, 7, day, h, m, s, ms, DateTimeKind.Utc);

    [Fact]
    public void Logout_StopsTheClock_TheNextLoginReadsWhatItLeft_Archive007b335e()
    {
        
        
        
        
        
        const uint charId = 0x7FB1_5B40;
        var rows = KharaRows.Value;
        try
        {
            KharaTitleClock.Seed(charId, null, At(8, 21, 6, 40, 375));
            KharaTitleClock.Resume(charId, At(8, 21, 6, 40, 375));
            var state = new KharaTitleState(0x23C, 0x268, null, null);
            (DateTime When, uint A, uint B, string Ans, string Nfy)[] selects =
            [
                (At(8, 21, 9, 31, 701), 0x23C, 0x26B, "3c020000c0d401006b02000000000000", "113b86073c0200006b020000"),
                (At(8, 21, 16, 25, 498), 0x23C, 0x268, "3c020000c0d401006802000000000000", "113b86073c02000068020000"),
                (At(8, 21, 18, 50, 515), 0x23C, 0x26B, "3c020000c0d401006b02000000000000", "113b86073c0200006b020000"),
            ];
            foreach (var (when, a, b, ans, nfy) in selects)
            {
                var d = KharaTitleStore.Decide(state, a, b, KharaTitleClock.PlayNow(charId, when), Any);
                Assert.Equal(ans, Hex(KharaTitleSelectPackets.Build2582(a, b, d)));
                Assert.Equal(nfy, Hex(KharaTitleSelectPackets.Build2592(0x07863B11u, d)));
                state = d.NewState;
            }
            KharaTitleClock.Pause(charId, At(8, 21, 20, 31, 742));                  
            KharaTitleStore.Publish(charId, state, rows);

            var stored = KharaTitleClock.PlayNow(charId, At(8, 21, 37, 27, 909));   
            KharaTitleClock.Forget(charId);                                          
            KharaTitleClock.Seed(charId, stored, At(8, 21, 37, 28, 846));
            KharaTitleClock.Resume(charId, At(8, 21, 37, 28, 846));
            using var head = new PacketWriter();
            KharaTitleStore.WriteHeadTitleA(head, charId, At(8, 21, 37, 28, 846));
            var body = head.ToArray();
            Assert.Equal("55490000ce000000" + new string('0', 20), Hex(body));     
            Assert.InRange(BinaryPrimitives.ReadUInt32LittleEndian(body), 18781u - 1500u, 18781u + 1500u);

            KharaTitleClock.Pause(charId, At(8, 21, 54, 54, 195));                  
            KharaTitleClock.Resume(charId, At(8, 21, 58, 22, 80));
            using var next = new PacketWriter();
            KharaTitleStore.WriteHeadTitleA(next, charId, At(8, 21, 58, 22, 80));
            Assert.Equal("00000000ce000000" + new string('0', 20), Hex(next.ToArray()));
        }
        finally
        {
            KharaTitleStore.Publish(charId, default, rows);
            KharaTitleClock.Forget(charId);
        }
    }

    [Fact]
    public void Colosseum_StopsTheClock_Archive15100617Fb30()
    {
        
        
        
        
        
        
        const uint charId = 0x7FB1_FB30;
        var rows = KharaRows.Value;
        try
        {
            KharaTitleClock.Seed(charId, null, At(18, 18, 31, 20, 661));
            KharaTitleClock.Resume(charId, At(18, 18, 31, 20, 661));
            KharaTitleClock.OnMapLoaded(charId, MapType(4109), At(18, 18, 31, 21, 591));
            Assert.False(KharaTitleClock.IsRunning(charId));

            var sel = KharaTitleStore.Decide(new KharaTitleState(0x24C, 0x1F1, null, null), 0x24C, 0x22B,
                KharaTitleClock.PlayNow(charId, At(18, 18, 37, 35, 529)), Any);
            Assert.Equal("4c020000c0d401002b02000000000000", Hex(KharaTitleSelectPackets.Build2582(0x24C, 0x22B, sel)));
            Assert.Equal("11ca92084c0200002b020000", Hex(KharaTitleSelectPackets.Build2592(0x0892CA11u, sel)));
            foreach (var t in new[] { (18, 43, 15, 629), (18, 43, 17, 548), (18, 43, 21, 383), (18, 43, 21, 538), (18, 43, 21, 701) })
            {
                var refused = KharaTitleStore.Decide(sel.NewState, 0x256, 0x22B,
                    KharaTitleClock.PlayNow(charId, At(18, t.Item1, t.Item2, t.Item3, t.Item4)), Any);
                Assert.False(refused.Accepted);
                Assert.Equal("56020000102700002b020000f7540000", Hex(KharaTitleSelectPackets.Build2582(0x256, 0x22B, refused)));
                Assert.Equal(sel.NewState, refused.NewState);
            }

            KharaTitleClock.Pause(charId, At(18, 18, 59, 54, 491));                 
            KharaTitleStore.Publish(charId, sel.NewState, rows);
            KharaTitleClock.Resume(charId, At(18, 18, 59, 54, 682));
            using var head = new PacketWriter();
            KharaTitleStore.WriteHeadTitleA(head, charId, At(18, 18, 59, 54, 682));
            Assert.Equal("c0d40100d7000000" + new string('0', 20), Hex(head.ToArray())); 
            KharaTitleClock.OnMapLoaded(charId, MapType(Prontera), At(18, 18, 59, 57, 542)); 
            Assert.True(KharaTitleClock.IsRunning(charId));
        }
        finally
        {
            KharaTitleStore.Publish(charId, default, rows);
            KharaTitleClock.Forget(charId);
        }
    }

    [Fact]
    public void PlayClock_OnlyMap4109StopsIt_AndAnUnknownCharacterReadsTheWallClock()
    {
        
        Assert.Equal(KharaTitleClock.PausedMapType, MapType(4109));
        Assert.NotEqual(KharaTitleClock.PausedMapType, MapType(4110));
        Assert.NotEqual(KharaTitleClock.PausedMapType, MapType(Prontera));
        Assert.NotEqual(KharaTitleClock.PausedMapType, MapType(InstanceMap));

        var wall = At(20, 12, 0, 0, 0);
        Assert.Equal(wall, KharaTitleClock.PlayNow(0x7FB1_0000u, wall));
        const uint charId = 0x7FB1_0001u;
        try
        {
            KharaTitleClock.Seed(charId, wall.AddHours(-3), wall);          
            Assert.Equal(wall.AddHours(-3), KharaTitleClock.PlayNow(charId, wall.AddMinutes(5)));
            KharaTitleClock.Resume(charId, wall.AddMinutes(5));
            Assert.Equal(wall.AddHours(-3).AddSeconds(30), KharaTitleClock.PlayNow(charId, wall.AddMinutes(5).AddSeconds(30)));
            KharaTitleClock.Seed(charId, wall.AddHours(-4), wall.AddMinutes(6)); 
            Assert.Equal(wall.AddHours(-3).AddSeconds(60), KharaTitleClock.PlayNow(charId, wall.AddMinutes(6)));
        }
        finally
        {
            KharaTitleClock.Forget(charId);
        }
    }

    [Fact]
    public void TitleOf_TakesTheRetailBlock()
    {
        var rows = KharaRows.Value;
        
        Assert.Equal(155u, KharaTitleStore.TitleOf(410, rows));
        Assert.Equal(206u, KharaTitleStore.TitleOf(572, rows));   
        Assert.Equal(234u, KharaTitleStore.TitleOf(616, rows));   
        Assert.Equal(237u, KharaTitleStore.TitleOf(619, rows));   
        Assert.Equal(215u, KharaTitleStore.TitleOf(588, rows));   
        Assert.Equal(146u, KharaTitleStore.TitleOf(401, rows));   
        Assert.Equal(0u, KharaTitleStore.TitleOf(420, rows));     
        Assert.Equal(247, KharaTitleStore.TitleGrantingKharas(rows).Count);
    }

    
    private static readonly JsonSerializerOptions DataJsonOptions =
        new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };

    private static readonly Lazy<List<KharaInfo>> KharaRows = new(() =>
        JsonSerializer.Deserialize<List<KharaInfo>>(File.ReadAllText(DataFile("Khara_Info.json")), DataJsonOptions)!);

    private static readonly Lazy<Dictionary<uint, byte>> MapTypes = new(() =>
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(DataFile("Map_List.json")));
        var types = new Dictionary<uint, byte>();
        foreach (var row in doc.RootElement.EnumerateArray())
            types.TryAdd(row.GetProperty("Map_Index").GetUInt32(), row.GetProperty("Map_Type").GetByte());
        return types;
    });

    private static byte? MapType(uint mapId) => MapTypes.Value.TryGetValue(mapId, out var t) ? t : null;

    private static string DataFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "data", name)))
            dir = dir.Parent;
        if (dir == null)
            throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/" + name);
        return Path.Combine(dir.FullName, "data", name);
    }
}
