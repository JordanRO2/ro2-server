using System.Buffers.Binary;
using System.Text.Json;
using RO2.GameData;
using RO2.GameData.Tables;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Byte-layout gate for RMI 3030 NfyWorldEventTrigger, the world-event burst sent after each 1304 AnsMapLoading
/// and on stage changes.
/// </summary>
/// <remarks>
/// 3030 uses the U32 envelope with no msgTag header. A builder that used the compact-length envelope of most
/// notifies would shift every field by two bytes while still forming a well-formed frame, so only a byte
/// comparison catches it. Each expected body is a fixed byte sequence, and the burst order is pinned: the data
/// table lists event 122101 first, but the burst sends 110001 first. The inner length is 8 + 13 per record.
/// </remarks>
public class WorldEventTrigger3030Tests
{
    private const string Retail110001Stage1 = "08000000b1ad010001010000";
    private const string Retail122101Stage1 = "08000000f5dc010001010000";

    private static readonly JsonSerializerOptions TableOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void Build3030_ReproducesBothReferenceFrames_ByteForByte()
    {
        Assert.Equal(Convert.FromHexString(Retail110001Stage1),
            WorldEventPackets.BuildWorldEventTrigger3030(110001, 1));
        Assert.Equal(Convert.FromHexString(Retail122101Stage1),
            WorldEventPackets.BuildWorldEventTrigger3030(122101, 1));
    }

    [Fact]
    public void Build3030_UsesTheU32Envelope_WithInnerLenCoveringTheRestOfTheBody()
    {
        var body = WorldEventPackets.BuildWorldEventTrigger3030(122101, 1);
        Assert.Equal(WorldEventPackets.NoRecordsBodyLen, body.Length);
        Assert.Equal((uint)(body.Length - 4), BinaryPrimitives.ReadUInt32LittleEndian(body));
        Assert.Equal(WorldEventPackets.NoRecordsBlobLen, BinaryPrimitives.ReadUInt32LittleEndian(body));
    }

    [Fact]
    public void Build3030_PutsTheStageAtBlobPlus5_AsInTheArchiveStage4Frames()
    {
        
        Assert.Equal(Convert.FromHexString("08000000f5dc010001040000"),
            WorldEventPackets.BuildWorldEventTrigger3030(122101, 4));
    }

    [Fact]
    public void InitialStages_TakesTheReturnStageZeroRow_AndSortsById()
    {
        
        
        var rows = new[]
        {
            new WorldEventMain { World_Event_ID = 122101, Stage_Level = 2, Return_Stage = 1 },
            new WorldEventMain { World_Event_ID = 122101, Stage_Level = 1, Return_Stage = 0 },
            new WorldEventMain { World_Event_ID = 110001, Stage_Level = 5, Return_Stage = 4 },
            new WorldEventMain { World_Event_ID = 110001, Stage_Level = 1, Return_Stage = 0 },
        };

        var stages = WorldEventPackets.InitialStages(rows);

        Assert.Equal(new[] { (110001u, (byte)1), (122101u, (byte)1) }, stages.ToArray());
    }

    [Fact]
    public void ShippedTable_OffTheEventMaps_ProducesRetailsStage1Burst()
    {
        
        var burst = Burst(Tables(), (110001, 1), (122101, 1), mapId: 1100);

        Assert.Equal(
            new[] { Retail110001Stage1, Retail122101Stage1, Retail110001Stage1, Retail122101Stage1 },
            burst);
    }

    [Fact]
    public void WorldEntry_OnMap1115AtStage4_CarriesTheBgmRecordInBothPasses()
    {
        
        const string bgm = "15000000f5dc010002040100045b0400007a03110000000000"; 
        var burst = Burst(Tables(), (110001, 1), (122101, 4), mapId: 1115);

        Assert.Equal(new[]
        {
            Retail110001Stage1, "08000000f5dc010001040000", bgm,
            Retail110001Stage1, "08000000f5dc010001040000", bgm,
        }, burst);
    }

    [Fact]
    public void WorldEntry_OnMap1221AtStage5_PutsTheNoticeBeforeTheBgm_AndSendsNoTrailer()
    {
        
        const string records = "22000000f5dc01000205020006c5040000853e00000000000004c50400008ba1120000000000";
        var burst = Burst(Tables(), (110001, 1), (122101, 5), mapId: 1221);

        Assert.Equal(new[]
        {
            Retail110001Stage1, "08000000f5dc010001050000", records,
            Retail110001Stage1, "08000000f5dc010001050000", records,
        }, burst);
    }

    [Fact]
    public void WorldEntry_OffTheEventMapsAtStage5_EndsWithTheAnnouncement()
    {
        
        var burst = Burst(Tables(), (110001, 1), (122101, 5), mapId: 1300);

        Assert.Equal(new[]
        {
            Retail110001Stage1, "08000000f5dc010001050000",
            Retail110001Stage1, "08000000f5dc010001050000",
            "15000000f5dc01000205010006c5040000853e000000000000",
        }, burst);
    }

    [Fact]
    public void WorldEntry_OnMap1115AtStage5_HasItsBgm_ThenTheAnnouncementOfMap1221()
    {
        
        const string bgm = "15000000f5dc010002050100045b0400007b03110000000000"; 
        var burst = Burst(Tables(), (110001, 1), (122101, 5), mapId: 1115);

        Assert.Equal(new[]
        {
            Retail110001Stage1, "08000000f5dc010001050000", bgm,
            Retail110001Stage1, "08000000f5dc010001050000", bgm,
            "15000000f5dc01000205010006c5040000853e000000000000",
        }, burst);
    }

    [Fact]
    public void StageChange_OnTheEventMap_SendsTheStageThenItsRecords()
    {
        
        
        var t = Tables();
        var push = WorldEventPackets.BuildStageChangePush(122101, 5, t.EventRows(t.Stage(122101, 5)!), 1221);

        Assert.Equal(new[]
        {
            "08000000f5dc010001050000",
            "3c000000f5dc01000205040003c5040000b53d00000000000004c50400008ba112000000000005c50400000200000000000000" +
            "06c5040000853e000000000000",
        }, push.Select(b => Convert.ToHexStringLower(b)).ToArray());

        
        var toStage3 = WorldEventPackets.BuildStageChangePush(122101, 3, t.EventRows(t.Stage(122101, 3)!), 1221);
        Assert.Equal(new[]
        {
            "08000000f5dc010001030000",
            "2f000000f5dc01000203030003c5040000b13d00000000000004c50400008aa112000000000005c50400000200000000000000",
        }, toStage3.Select(b => Convert.ToHexStringLower(b)).ToArray());
    }

    [Fact]
    public void StageChange_OffTheEventMaps_SendsStageZeroOnly()
    {
        
        
        var t = Tables();
        foreach (var (stage, map) in new[] { ((byte)5, 1101u), ((byte)1, 1100u), ((byte)1, 3101u) })
        {
            var push = WorldEventPackets.BuildStageChangePush(122101, stage, t.EventRows(t.Stage(122101, stage)!), map);
            Assert.Equal(new[] { "08000000f5dc010001000000" }, push.Select(b => Convert.ToHexStringLower(b)).ToArray());
        }
    }

    private static string[] Burst(WorldEventTables t, (uint Id, byte Stage) first, (uint Id, byte Stage) second, uint mapId)
    {
        var events = new[] { first, second }
            .Select(e => (e.Id, e.Stage, t.EventRows(t.Stage(e.Id, e.Stage)!)))
            .ToList();
        return WorldEventPackets.BuildWorldEntryBurst(events, mapId).Select(b => Convert.ToHexStringLower(b)).ToArray();
    }

 /// <summary>
 /// Loads the three world-event tables with the loader options but without GameDataStore.LoadAsync, which would
 /// replace the process-wide store while tests run in parallel.
 /// </summary>
    internal static WorldEventTables Tables() => new(
        Table<WorldEventMain>("WorldEvent_Main.json"),
        Table<WorldEventCheckGroup>("WorldEvent_Check_Group.json"),
        Table<WorldEventEventGroup>("WorldEvent_Event_Group.json"));

    internal static List<T> Table<T>(string file)
        => JsonSerializer.Deserialize<List<T>>(File.ReadAllBytes(RequireDataFile(file)), TableOptions)!;

 /// <summary>
 /// Walks up from the test output path to the repository data directory. The tables are tracked and the Zone
 /// server loads them at startup, so a missing file is a defect, not a skip.
 /// </summary>
    internal static string RequireDataFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data", name);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/" + name);
    }
}

/// <summary>
/// Function-object frame of RMI 2512 OnFuncObject_In: the map's function objects from ZoneSpawn
/// (FuncObjectRegistry), encoded as FuncObj42 records (FuncObjectPackets).
/// </summary>
/// <remarks>
/// Templates in FuncObjectRegistry.RetailUnlistedTemplates are never listed. Function objects whose condition
/// requires a quest are listed regardless of the character's quest progress.
/// </remarks>
public class FuncObjectIn2512Tests
{
    private const string RetailThreeObjects =
        "0288008800eee3000000000300140c0000e0ddc4048e0e0d449d454844486c824201000000000000000000000000000000000000000000" +
        "140d0000e0ddc40480c20d44a4c94844486c824201000000000000000000000000000000000000000000140e0000e0ddc404edce0c44f5" +
        "054944486c824201000000000000000000000000000000000000000000";

    private static readonly JsonSerializerOptions TableOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void ZoneSpawnRows_ReproduceRetailsFrame()
    {
        var spawns = ZoneSpawn().Where(s => s.MapId == 1100).ToList();
        var rows = new[] { 1063, 1062, 1061 }.Select(i => spawns.Single(s => s.Index == i)).ToList();
        var handles = new Queue<uint>(new uint[] { 0x0C14, 0x0D14, 0x0E14 });

        var objects = FuncObjectRegistry.Build(rows, ListedIds(), _ => false, handles.Dequeue);

        Assert.Equal(RetailThreeObjects, Convert.ToHexStringLower(FuncObjectPackets.BuildFuncObjectIn2512(objects)));
    }

    [Fact]
    public void NoObjects_IsTheCountZeroForm_ThisServerSentBefore()
    {
        var empty = FuncObjectPackets.BuildMapLoadFrames2512(Array.Empty<FuncObject>());
        Assert.Single(empty);
        Assert.Equal("010a0a00eee3000000000000", Convert.ToHexStringLower(empty[0]));
        Assert.Equal(ActorStatePackets.BuildFuncObjectInEmpty2512(), empty[0]);
    }

    [Fact]
    public void Map1101_ListsItsFunctionObjectRows_WithUniqueFunctionObjectHandles()
    {
        uint serial = 0;
        var objects = FuncObjectRegistry.Build(ZoneSpawn().Where(s => s.MapId == 1101), ListedIds(), _ => false,
            () => (++serial << 8) | FuncObjectRegistry.FuncObjectActorTag);

        
        Assert.Equal(442, objects.Count);
        Assert.All(objects, o => Assert.Equal(FuncObjectRegistry.FuncObjectActorTag, (byte)o.Handle));
        Assert.Equal(objects.Count, objects.Select(o => o.Handle).Distinct().Count());

        
        var frames = FuncObjectPackets.BuildMapLoadFrames2512(objects);
        Assert.Equal(6, frames.Count);
        int total = 0;
        foreach (var f in frames)
        {
            var n = BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(3 + 8));
            Assert.InRange(n, 1, FuncObjectPackets.MaxRecordsPerFrame);
            Assert.Equal(10 + 42 * n, BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(1)));
            total += n;
        }
        Assert.Equal(442, total);
    }

    [Fact]
    public void Map1100_LeavesOutTheTemplatesRetailNeverLists_AndKeepsTheQuestConditionedOnes()
    {
        uint serial = 0;
        var spawns = ZoneSpawn().Where(s => s.MapId == 1100).ToList();
        var objects = FuncObjectRegistry.Build(spawns, ListedIds(), _ => false,
            () => (++serial << 8) | FuncObjectRegistry.FuncObjectActorTag);

        Assert.Equal(50, spawns.Count(s => FuncObjectIds().Contains(s.AssetIndex)));
        
        Assert.Equal(19, objects.Count);
        Assert.DoesNotContain(objects, o => o.TemplateId is 80011073 or 82460000 or 80013090 or 80190000);
        
        Assert.Equal(8, objects.Count(o => o.TemplateId == 80010720));
    }

    [Fact]
    public void RetailUnlistedTemplates_AreFunctionObjectTemplatesWithSpawnRows()
    {
        var ids = FuncObjectIds();
        var spawns = ZoneSpawn();
        var spawned = spawns.Select(s => s.AssetIndex).ToHashSet();

        Assert.Equal(69, FuncObjectRegistry.RetailUnlistedTemplates.Count);
        Assert.All(FuncObjectRegistry.RetailUnlistedTemplates, t =>
        {
            Assert.Contains(t, ids);
            Assert.Contains(t, spawned);
        });
        Assert.Equal(268, spawns.Count(s => FuncObjectRegistry.RetailUnlistedTemplates.Contains(s.AssetIndex)));
        Assert.Equal(ids.Count - 69, ListedIds().Count);
    }

    [Fact]
    public void Build_SkipsRowsThatAreNpcTemplates()
    {
        var rows = ZoneSpawn().Where(s => s.MapId == 1100 && s.Index is 1061 or 1062).ToList();
        uint next = 0x0114;
        var objects = FuncObjectRegistry.Build(rows, FuncObjectIds(), id => true, () => next++);
        Assert.Empty(objects);
    }

    private static List<SpawnEntry> ZoneSpawn()
        => JsonSerializer.Deserialize<List<SpawnEntry>>(
            File.ReadAllBytes(WorldEventTrigger3030Tests.RequireDataFile("ZoneSpawn.json")), TableOptions)!;

    private static HashSet<uint> FuncObjectIds()
        => WorldEventTrigger3030Tests.Table<FuncObjectInfo>("FuncObjectInfo.json").Select(r => r.ID).ToHashSet();

 /// <summary>Template ids listed by FuncObjectRegistry.ForMap: FuncObjectInfo entries less the unlisted templates.</summary>
    private static IReadOnlySet<uint> ListedIds()
        => FuncObjectRegistry.ListedTemplateIds(WorldEventTrigger3030Tests.Table<FuncObjectInfo>("FuncObjectInfo.json"));
}
