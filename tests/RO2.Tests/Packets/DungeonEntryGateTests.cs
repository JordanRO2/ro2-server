using RO2.Persistence.Mongo.Repositories;
using RO2.Domain.GameData;
using RO2.Domain.GameData.Tables;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Tests the dungeon entry checks (<see cref="DungeonEntryGate"/>): the 1521 and 1531 entry rules, the 2556
/// refusal, the 3035 and 2771 bodies, and the daily entry-count window
/// (<see cref="DungeonEntryCountRepository"/>).
/// </summary>
public class DungeonEntryGateTests
{
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    [Theory]
    
    [InlineData(0x004CF01Fu, (ushort)19015, "1ff04c00474a")]
    
    
    [InlineData(0x004D2CFCu, (ushort)19001, "fc2c4d00394a")]
    public void Refusal_IsRetailsLone2556(uint account, ushort code, string retailHead)
    {
        var body = DungeonEntryGate.BuildRefusal2556(account, code);

        Assert.Equal(DungeonChannelHandOff.AnsChannelMoveBytes, body.Length);
        Assert.Equal(retailHead, Hex(body.AsSpan(0, 6).ToArray()));
        
        Assert.All(body.AsSpan(6).ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task LevelGate_RefusesBelowMapMinLevel_As19001()
    {
        var data = await DungeonDataAsync();
        var row = Row(data, 1, 2); 
        Assert.Equal(5, row.DungeonID);

        
        Assert.Equal(DungeonEntryGate.ResultWrongLevel, DungeonEntryGate.CheckInstanceEntry(row, 1, true, true, 0));
        Assert.Equal(0, DungeonEntryGate.CheckInstanceEntry(row, row.Map_Min_Level, true, true, 0));
        Assert.Equal(0, DungeonEntryGate.CheckInstanceEntry(row, 50, true, true, 0));
    }

    [Fact]
    public async Task QuestGate_RefusesAnIncompleteNeedQuest_As19012()
    {
        var data = await DungeonDataAsync();
        var row = Row(data, 20, 3); 
        Assert.Equal(40, row.DungeonID);
        Assert.NotEqual(0u, row.Need_Quest_ID);

        
        Assert.Equal(DungeonEntryGate.ResultQuestRequired, DungeonEntryGate.CheckInstanceEntry(row, 50, false, true, 0));
        Assert.Equal(0, DungeonEntryGate.CheckInstanceEntry(row, 50, true, true, 0));
    }

    [Fact]
    public async Task CountGate_RefusesOnlyAnEntryThatWouldCount_As19015()
    {
        var data = await DungeonDataAsync();
        var row = Row(data, 42, 1); 
        Assert.Equal(64, row.DungeonID);
        Assert.Equal(2, row.Enter_Count);

        
        Assert.Equal(DungeonEntryGate.ResultEnterCountExceeded, DungeonEntryGate.CheckInstanceEntry(row, 50, true, true, 2));
        Assert.Equal(DungeonEntryGate.ResultEnterCountExceeded, DungeonEntryGate.CheckEnterCount(row, true, 2));
        
        Assert.Equal(0, DungeonEntryGate.CheckInstanceEntry(row, 50, true, true, 1));
        
        
        Assert.Equal(0, DungeonEntryGate.CheckInstanceEntry(row, 50, true, false, 2));
        
        Assert.Equal(0, DungeonEntryGate.CheckEnterCount(Row(data, 42, 3) with { Enter_Count = 0 }, true, 99));
    }

    [Fact]
    public async Task EntryControl_RefusesTheTrainingGroundToAPlayerInNoParty_As19004()
    {
        var data = await DungeonDataAsync();
        var row = Row(data, 21, 1); 
        var arrival = data.Positions[row.Position_ID];
        var map = arrival.Area_ID;
        Assert.Equal(4110u, map);
        Assert.Equal(5, data.GetMap(map)!.Map_Type);
        Assert.True(DungeonEntryGate.IsEntryControlled(data, map));
        
        Assert.Equal((100f, 100f, 100f, 0), (arrival.Position_X, arrival.Position_Y, arrival.Position_Z, arrival.Direction));

        
        
        Assert.Equal(DungeonEntryGate.ResultInstanceMapFailed, DungeonEntryGate.CheckEntryControl(data, map, inParty: false));
        
        
        Assert.Equal(0, DungeonEntryGate.CheckEntryControl(data, map, inParty: true));
        
        Assert.False(DungeonEntryGate.IsEntryControlled(data, 426101));
        Assert.Equal(0, DungeonEntryGate.CheckEntryControl(data, 426101, inParty: false));
    }

    [Fact]
    public void TrainingRoom_OpensOnlyWhenNobodyButTheRequesterIsOnTheMap()
    {
        var leader = new PlayerState { SessionId = 7, MapId = 1111 };
        var member = new PlayerState { SessionId = 8, MapId = 4110 };
        var otherTeam = Enumerable.Range(20, 5).Select(i => new PlayerState { SessionId = (uint)i, MapId = 4110 }).ToArray();

        
        Assert.True(DungeonEntryGate.OpensTrainingRoom([], leader.SessionId));
        Assert.True(DungeonEntryGate.OpensTrainingRoom([leader], leader.SessionId));
        
        Assert.False(DungeonEntryGate.OpensTrainingRoom(otherTeam, leader.SessionId));
        
        Assert.False(DungeonEntryGate.OpensTrainingRoom([member], leader.SessionId));
    }

    [Theory]
    [InlineData(42, 1, (byte)1)]  
    [InlineData(5, 3, (byte)1)]   
    [InlineData(27, 1, (byte)8)]  
    [InlineData(35, 1, (byte)8)]  
    public async Task MoveKind_FollowsTheDestinationsMapType(int group, int level, byte mapType)
    {
        var data = await DungeonDataAsync();
        var row = Row(data, group, level);
        Assert.Equal(mapType, data.GetMap(data.Positions[row.Position_ID].Area_ID)!.Map_Type);
    }

    [Theory]
    
    
    [InlineData(0x40u, (ushort)2, (ushort)2, "4000000002000200")]
    [InlineData(0x42u, (ushort)1, (ushort)2, "4200000001000200")]
    [InlineData(0x41u, (ushort)0, (ushort)2, "4100000000000200")]
    public void EntranceInfo3035_IsRetailsBody(uint dungeonId, ushort used, ushort limit, string retail)
        => Assert.Equal(retail, Hex(DungeonEntranceInfoHandler.Build3035(dungeonId, used, limit)));

    [Theory]
    [InlineData(0x40u, "40000000")] 
    [InlineData(0x08u, "08000000")] 
    [InlineData(0x3Fu, "3f000000")] 
    public void Reset2771_IsRetailsBody(uint dungeonId, string retail)
        => Assert.Equal(retail, Hex(DungeonResetHandler.Build2771(dungeonId)));

    [Fact]
    public void CountWindow_StartsAt1000Utc()
    {
        
        Assert.Equal(new DateTime(2026, 7, 10, 10, 0, 0, DateTimeKind.Utc),
            DungeonEntryCountRepository.WindowStart(new DateTime(2026, 7, 11, 2, 50, 45, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 7, 11, 10, 0, 0, DateTimeKind.Utc),
            DungeonEntryCountRepository.WindowStart(new DateTime(2026, 7, 11, 12, 21, 55, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 7, 11, 10, 0, 0, DateTimeKind.Utc),
            DungeonEntryCountRepository.WindowStart(new DateTime(2026, 7, 11, 10, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 7, 10, 10, 0, 0, DateTimeKind.Utc),
            DungeonEntryCountRepository.WindowStart(new DateTime(2026, 7, 11, 9, 59, 59, DateTimeKind.Utc)));
    }

    [Fact]
    public void CountWindow_AStoredCountReadsZeroOnceItsWindowHasPassed()
    {
        var doc = new DungeonEntryCountDocument
        {
            Id = 1,
            WindowStartUtc = new DateTime(2026, 7, 10, 10, 0, 0, DateTimeKind.Utc),
            Counts = { ["64"] = 2 },
        };
        Assert.Equal(2, DungeonEntryCountRepository.UsedIn(doc, 64, new DateTime(2026, 7, 11, 2, 50, 45, DateTimeKind.Utc)));
        Assert.Equal(0, DungeonEntryCountRepository.UsedIn(doc, 65, new DateTime(2026, 7, 11, 2, 50, 45, DateTimeKind.Utc)));
        Assert.Equal(0, DungeonEntryCountRepository.UsedIn(doc, 64, new DateTime(2026, 7, 11, 12, 21, 55, DateTimeKind.Utc)));
        Assert.Equal(0, DungeonEntryCountRepository.UsedIn(null, 64, DateTime.UtcNow));
    }

    internal static DungeonInfo Row(GameDataStore data, int group, int level)
        => Assert.Single(data.DungeonInfo, d => d.DungeonGroupID == group && d.Map_Level == level);

 /// <summary>The tables the checks read, copied from data/ into a private directory.</summary>
    internal static async Task<GameDataStore> DungeonDataAsync()
    {
        var source = RequireDataDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "ro2-dungeongate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var table in new[] { "DungeonInfo.json", "Position_Info.json", "Map_List.json", "Entry_control.json" })
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
            if (File.Exists(Path.Combine(candidate, "DungeonInfo.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/DungeonInfo.json");
    }
}
