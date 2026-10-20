using System.Text.Json;
using RO2.Domain.GameData;
using Xunit;

namespace RO2.Tests.GameData;

/// <summary>
/// Tests the navmesh cell lookup used for NPC placement. Results must stay within the known cell range and never be -1, because the client indexes its navmesh with the value unchecked.
/// </summary>
/// <remarks>
/// Two sources are used: the observed wire table, which takes precedence, and the table computed from the client navmesh, which fills gaps.
/// The real-data tests confirm that the shipped tables still agree.
/// </remarks>
public class NavCellLookupTests
{
    private static async Task<GameDataStore> LoadAsync(
        string? measuredJson, string? meshJson)
    {
        
        
        var dir = Path.Combine(Path.GetTempPath(), "ro2-navcell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            if (measuredJson != null)
                await File.WriteAllTextAsync(Path.Combine(dir, "NpcNavCell.json"), measuredJson);
            if (meshJson != null)
                await File.WriteAllTextAsync(Path.Combine(dir, "NavCellFromMesh.json"), meshJson);

            var store = new GameDataStore();
            await store.LoadAsync(dir);
            return store;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task WireMeasuredCell_BeatsMeshCell_ForTheSameSpawn()
    {
        
        
        var store = await LoadAsync(
            measuredJson: """{"1300:383":421}""",
            meshJson: """{"1300:383":999}""");

        Assert.Equal(421, store.GetNavCell(1300, 383));
        Assert.Equal(1, store.MeasuredNavCellCount);
        Assert.Equal(0, store.MeshNavCellCount);
    }

    [Fact]
    public async Task MeshCell_FillsSpawnsRetailNeverRevealed()
    {
        var store = await LoadAsync(
            measuredJson: """{"1100:10":941}""",
            meshJson: """{"1100:10":941,"1100:11":940,"1100:24":606}""");

        Assert.Equal(941, store.GetNavCell(1100, 10));   
        Assert.Equal(940, store.GetNavCell(1100, 11));   
        Assert.Equal(606, store.GetNavCell(1100, 24));   
        Assert.Equal(1, store.MeasuredNavCellCount);
        Assert.Equal(2, store.MeshNavCellCount);
    }

    [Fact]
    public async Task CellsOutsideRetailsObservedRange_AreDropped_FromEitherSource()
    {
        
        
        var store = await LoadAsync(
            measuredJson: JsonSerializer.Serialize(new Dictionary<string, int>
            {
                ["1:1"] = GameDataStore.MaxKnownNavCell + 1,
                ["1:2"] = -1,
            }),
            meshJson: JsonSerializer.Serialize(new Dictionary<string, int>
            {
                ["1:3"] = int.MaxValue,
                ["1:4"] = GameDataStore.MaxKnownNavCell,
            }));

        Assert.Null(store.GetNavCell(1, 1));
        Assert.Null(store.GetNavCell(1, 2));
        Assert.Null(store.GetNavCell(1, 3));
        Assert.Equal(GameDataStore.MaxKnownNavCell, store.GetNavCell(1, 4));
        Assert.Equal(0, store.MeasuredNavCellCount);
        Assert.Equal(1, store.MeshNavCellCount);
    }

    [Fact]
    public async Task MalformedKeys_AreIgnored_WithoutTakingTheRestDown()
    {
        var store = await LoadAsync(
            measuredJson: """{"nope":1,":5":2,"1100":3,"1100:":4,"1100:7":8}""",
            meshJson: null);

        Assert.Equal(8, store.GetNavCell(1100, 7));
        Assert.Equal(1, store.MeasuredNavCellCount);
    }

    [Fact]
    public async Task MissingFiles_LeaveTheTableEmptyRatherThanThrowing()
    {
        var store = await LoadAsync(measuredJson: null, meshJson: null);

        Assert.Empty(store.NpcNavCells);
        Assert.Null(store.GetNavCell(1100, 10));
        Assert.Equal(0, store.MeasuredNavCellCount);
        Assert.Equal(0, store.MeshNavCellCount);
    }

    [Fact]
    public void NavCellKey_SeparatesMapsThatShareASpawnIndex()
    {
        
        Assert.NotEqual(GameDataStore.NavCellKey(1100, 10), GameDataStore.NavCellKey(1101, 10));
        Assert.NotEqual(GameDataStore.NavCellKey(1100, 10), GameDataStore.NavCellKey(1100, 11));
        Assert.Equal(GameDataStore.NavCellKey(1100, 10), GameDataStore.NavCellKey(1100, 10));
    }

    
    
    
 /// <summary>
 /// Returns the repository's data directory. Fails the test when it is missing, because the data files are tracked.
 /// </summary>
    private static string RequireDataDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "NpcNavCell.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "no ancestor of " + AppContext.BaseDirectory + " holds data/NpcNavCell.json");
    }

    private static string RequireMeshTable()
    {
        var path = Path.Combine(RequireDataDirectory(), "NavCellFromMesh.json");
        Assert.True(File.Exists(path),
            "data/NavCellFromMesh.json is missing - regenerate it with: "
            + "python tools/parse_ground_tok.py");
        return path;
    }

    private static Dictionary<string, int> ReadTable(string path)
        => JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path))!;

    [Fact]
    public void ShippedMeshTable_StaysInsideTheRangeTheClientCanSurvive()
    {
        var mesh = ReadTable(RequireMeshTable());

        Assert.NotEmpty(mesh);
        foreach (var (_, cell) in mesh)
            Assert.InRange(cell, 0, GameDataStore.MaxKnownNavCell);
    }

    [Fact]
    public void ShippedMeshTable_ReproducesTheWireMeasurements()
    {
        
        
        
        
        
        var measured = ReadTable(Path.Combine(RequireDataDirectory(), "NpcNavCell.json"));
        var mesh = ReadTable(RequireMeshTable());

        var covered = 0;
        var agreed = 0;
        foreach (var (key, wireCell) in measured)
        {
            if (!mesh.TryGetValue(key, out var meshCell))
                continue;
            covered++;
            if (meshCell == wireCell)
                agreed++;
        }

        
        Assert.Equal(measured.Count, covered);
        Assert.True(agreed >= (int)(covered * 0.99),
            $"mesh table agrees with the wire on {agreed}/{covered} rows, below the 99% floor the "
            + "wiring decision was made on (measured 6,709/6,715 = 99.91%)");
    }

    [Fact]
    public async Task ShippedTables_ResolveTheRetailRecordUsedByTheByteLayoutGate()
    {
        
        
        
        var dataDir = RequireDataDirectory();

        Assert.Equal(421, ReadTable(Path.Combine(dataDir, "NpcNavCell.json"))["1300:383"]);
        Assert.Equal(421, ReadTable(RequireMeshTable())["1300:383"]);

        var store = new GameDataStore();
        await store.LoadAsync(dataDir);
        Assert.Equal(421, store.GetNavCell(1300, 383));
        Assert.InRange(store.GetNavCell(1300, 383)!.Value, 0, GameDataStore.MaxKnownNavCell);
    }
}
