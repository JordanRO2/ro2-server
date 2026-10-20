using System.Buffers.Binary;
using RO2.Domain.GameData;
using RO2.Domain.GameData.Tables;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// FuncObjectRegistry's per-map exclusion (RetailUnlistedOnMap): a template listed on one map and left out
/// of another is dropped from that map's 2512s only.
/// </summary>
/// <remarks>
/// Expected bodies are reference frames, byte for byte. Handles are the reference server's runtime counters,
/// set on the rows by hand; the other record fields come from ZoneSpawn through FuncObjectRegistry.Build.
///
/// Keying the exclusion on the template alone is wrong: 80010690 and 80010700 must be listed on Dayr Desert
/// 1301 and stay off Dark Whisper - Lower Floor 4302. Templates 1200 and 1201 are listed by the reference
/// server even though a count of 2512s per map load never sees their rows, so those rows must stay.
/// </remarks>
public class FuncObjectRegistryTests
{
 /// <summary>
 /// The 2311 that places the player on map 4302. Body offsets: map id at +10 (blob+8, behind the 2-byte
 /// compact envelope), X/Y/Z at +26, +30 and +34.
 /// </summary>
    private const string Retail2311Onto4302 =
        "012b2b0094e21183640dce10000000000000000000000000000033b3ce4366e6ba430080284300001183640d00";

 /// <summary>
 /// The 2512 answering a 1304: 80011020 (row 5) and 80011010 (row 4) are listed; the 80010690 rows 249 and
 /// 251, which stand nearer the player than row 5, are not.
 /// </summary>
    private const string Retail4302MapLoad =
        "015e5e00eee300000000020014a615000cdfc404c979c143e181c243df25264301000000000000000000000000000000000000000000" +
        "14a7150002dfc404ca47d143a0e1b743b429284301000000000000000000000000000000000000000000";

 /// <summary>On map 1301, template 80010700 rows 2706 and 2708 are listed.</summary>
    private const string Retail1301Two80010700 =
        "015e5e00eee300000000020014ad1200ccddc404a6afd343841a3c44fe16904101000000000000000000000000000000000000000000" +
        "14ac1200ccddc404465ae6430df03d4408f99b4101000000000000000000000000000000000000000000";

 /// <summary>
 /// Map 1301 rows 2607 and 2601 (80010670), 2677 and 2679 (80010690), and 2747 (82340004).
 /// </summary>
    private const string Retail1301With80010690 =
        "02dc00dc00eee300000000050014561200aeddc404c08457444a636844b83c7741010000000000000000000000000000000000000000" +
        "0014871200c2ddc4047cbd594463b86444e61d83410100000000000000000000000000000000000000000014132700aeddc40406c063" +
        "4449826544528b734101000000000000000000000000000000000000000000141b2700c2ddc404c5176844847d6244ec057441010000" +
        "0000000000000000000000000000000000000014881e00a468e804c9e86444ee796944b77c6441010000000000000000000000000000" +
        "00000000000000";

 /// <summary>
 /// The one row of 1200 (template 80070000, row 36). A count of 2512s per map load does not see this row.
 /// </summary>
    private const string Retail1200Row36 =
        "01343400eee300000000010014170a0070c5c50458071844048834446ea7744201000000000000000000000000000000000000000000";

 /// <summary>
 /// Rows 80010420 (1201 row 2) and 80432000 (row 1792), which follow a 2311 to map 4142 rather than 1201.
 /// </summary>
    private const string Retail1201Row2 =
        "015e5e00eee300000000020014280a00b4dcc4045b0f564340319e449b872a42010000ec420000000000000000000000000000000000" +
        "14ce0c00804bcb04a18255435b63a044c0d7214201000000000000000000000000000000000000000000";

    private static readonly Lazy<List<SpawnEntry>> ZoneSpawn =
        new(() => WorldEventTrigger3030Tests.Table<SpawnEntry>("ZoneSpawn.json"));

    private static readonly Lazy<List<FuncObjectInfo>> FuncObjectInfoTable =
        new(() => WorldEventTrigger3030Tests.Table<FuncObjectInfo>("FuncObjectInfo.json"));

    [Fact]
    public void Map4302_KeepsTheElevenRowsRetailLists_AndDropsBothGatheringTemplates()
    {
        var rows = MapRows(4302);
        var funcObjectIds = FuncObjectInfoTable.Value.Select(r => r.ID).ToHashSet();
        Assert.Equal(74, rows.Count(s => funcObjectIds.Contains(s.AssetIndex)));

        var objects = Build(rows);

        
        Assert.Equal(11, objects.Count);
        Assert.Equal(new[] { (80010970u, 1), (80011010u, 1), (80011020u, 1), (80011090u, 8) },
            objects.GroupBy(o => o.TemplateId).Select(g => (g.Key, g.Count())).OrderBy(p => p.Key).ToArray());
        Assert.DoesNotContain(objects, o => o.TemplateId is 80010690 or 80010700);
    }

    [Fact]
    public void Map4302_MapLoadReply_IsRetailsFrame_WithoutTheRowsNextToTheEntryPoint()
    {
        var map2311 = Convert.FromHexString(Retail2311Onto4302);
        Assert.Equal(4302u, BinaryPrimitives.ReadUInt32LittleEndian(map2311.AsSpan(10)));
        var x = BinaryPrimitives.ReadSingleLittleEndian(map2311.AsSpan(26));
        var y = BinaryPrimitives.ReadSingleLittleEndian(map2311.AsSpan(30));

        
        
        var rows = MapRows(4302);
        foreach (var index in new[] { 249, 251 })
        {
            var row = rows.Single(s => s.Index == index);
            Assert.Equal(80010690u, row.AssetIndex);
            Assert.InRange(MathF.Sqrt((row.PosX - x) * (row.PosX - x) + (row.PosY - y) * (row.PosY - y)), 0f, 30f);
        }

        
        var reply = Convert.FromHexString(Retail4302MapLoad);
        Assert.DoesNotContain(RecordTemplates(reply), t => t is 80010690 or 80010700);
        var objects = Build(rows);
        Assert.DoesNotContain(objects, o => o.SpawnIndex is 249 or 251);

        Assert.Equal(Retail4302MapLoad, Frame(objects, (5, 0x0015A614), (4, 0x0015A714)));
    }

    [Fact]
    public void Map1301_KeepsBothTemplates_AndReproducesRetailsFrames()
    {
        var objects = Build(MapRows(1301));

        
        Assert.Equal(35, objects.Count(o => o.TemplateId == 80010690));
        Assert.Equal(35, objects.Count(o => o.TemplateId == 80010700));

        Assert.Equal(Retail1301Two80010700, Frame(objects, (2706, 0x0012AD14), (2708, 0x0012AC14)));
        Assert.Equal(Retail1301With80010690, Frame(objects,
            (2607, 0x00125614), (2677, 0x00128714), (2601, 0x00271314), (2679, 0x00271B14), (2747, 0x001E8814)));
    }

    [Fact]
    public void Map1200_KeepsRow36_WhichRetailLists()
    {
        var objects = Build(MapRows(1200));

        Assert.Single(objects, o => o.TemplateId == 80070000);
        Assert.Equal(Retail1200Row36, Frame(objects, (36, 0x000A1714)));
    }

    [Fact]
    public void Map1201_KeepsTheFiveTemplatesRetailListsThere()
    {
        var objects = Build(MapRows(1201));

        
        
        Assert.Equal(11, objects.Count(o => o.TemplateId == 80100580));
        Assert.Equal(8, objects.Count(o => o.TemplateId == 80100850));
        Assert.Single(objects, o => o.TemplateId == 80010420);
        Assert.Single(objects, o => o.TemplateId == 80010340);
        Assert.Single(objects, o => o.TemplateId == 80060000);

        Assert.Equal(Retail1201Row2, Frame(objects, (2, 0x000A2814), (1792, 0x000CCE14)));
    }

    [Fact]
    public void RetailUnlistedOnMap_NamesTemplatesRetailListsOnAnotherMap()
    {
        var funcObjectIds = FuncObjectInfoTable.Value.Select(r => r.ID).ToHashSet();
        var spawns = ZoneSpawn.Value;

        Assert.Equal(2, FuncObjectRegistry.RetailUnlistedOnMap.Count);
        foreach (var (map, template) in FuncObjectRegistry.RetailUnlistedOnMap)
        {
            Assert.Contains(template, funcObjectIds);
            
            Assert.False(FuncObjectRegistry.RetailUnlistedTemplates.Contains(template),
                template + " is unlisted everywhere");
            Assert.Contains(spawns, s => s.MapId == map && s.AssetIndex == template);
            Assert.Contains(spawns, s => s.AssetIndex == template
                                         && !FuncObjectRegistry.RetailUnlistedOnMap.Contains((s.MapId, template)));
        }
        
        Assert.Equal(63, spawns.Count(s => FuncObjectRegistry.RetailUnlistedOnMap.Contains((s.MapId, s.AssetIndex))));
    }

    [Fact]
    public async Task ForMap_LeavesThePairsOutOnTheLivePath()
    {
        
        
        var dir = Path.Combine(Path.GetTempPath(), "ro2-funcobj-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var name in new[] { "ZoneSpawn.json", "FuncObjectInfo.json", "NPCInfo.json" })
                File.Copy(WorldEventTrigger3030Tests.RequireDataFile(name), Path.Combine(dir, name));
            var store = new GameDataStore();
            await store.LoadAsync(dir);

            var objects = FuncObjectRegistry.ForMap(store, 4302);

            Assert.Equal(11, objects.Count);
            Assert.DoesNotContain(objects, o => o.TemplateId is 80010690 or 80010700);
            Assert.All(objects, o => Assert.Equal(FuncObjectRegistry.FuncObjectActorTag, (byte)o.Handle));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

 /// <summary>One map's ZoneSpawn rows, in file order (as GameDataStore.GetMapSpawns groups them).</summary>
    private static List<SpawnEntry> MapRows(uint mapId)
        => ZoneSpawn.Value.Where(s => s.MapId == mapId && s.AssetIndex != 0).ToList();

 /// <summary>FuncObjectRegistry.Build with ForMap's template set and a plain counter for the handles.</summary>
    private static IReadOnlyList<FuncObject> Build(IEnumerable<SpawnEntry> rows)
    {
        uint serial = 0;
        return FuncObjectRegistry.Build(rows, FuncObjectRegistry.ListedTemplateIds(FuncObjectInfoTable.Value),
            _ => false, () => (++serial << 8) | FuncObjectRegistry.FuncObjectActorTag);
    }

 /// <summary>
 /// The 2512 body listing the objects of the given ZoneSpawn rows, in the given order, under the reference handles.
 /// </summary>
    private static string Frame(IReadOnlyList<FuncObject> objects, params (int SpawnIndex, uint Handle)[] records)
        => Convert.ToHexStringLower(FuncObjectPackets.BuildFuncObjectIn2512(
            records.Select(r => objects.Single(o => o.SpawnIndex == r.SpawnIndex) with { Handle = r.Handle })
                .ToList()));

 /// <summary>
 /// The template id (+4) of every FuncObj42 record of a 2512 body: [01 n] envelope, count at blob+8, records from
 /// blob+10.
 /// </summary>
    private static List<uint> RecordTemplates(byte[] body)
    {
        Assert.Equal(0x01, body[0]);
        var blob = body.AsSpan(2, body[1]);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(blob[8..]);
        var templates = new List<uint>(count);
        for (int i = 0; i < count; i++)
        {
            var record = blob[(10 + FuncObjectPackets.RecordLength * i)..];
            templates.Add(BinaryPrimitives.ReadUInt32LittleEndian(record[4..]));
        }
        return templates;
    }
}
