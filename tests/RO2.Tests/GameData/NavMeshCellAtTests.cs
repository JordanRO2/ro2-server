using System.IO.Compression;
using System.Text.Json;
using RO2.Domain.GameData;
using Xunit;

namespace RO2.Tests.GameData;

/// <summary>
/// Parity gate for the runtime point -> navmesh-cell solver (<see cref="NavMesh"/>), the C# port of
/// <c>NavMesh.cell_at</c> in tools/parse_ground_tok.py.
///
/// THE ARTIFACTS ARE ASSUMED TO EXIST. data/navmesh/&lt;mapId&gt;.nav is tracked in the repo (like
/// data/NavCellFromMesh.json) and regenerated with <c>python tools/export_navmesh.py</c>; a missing
/// file is a real defect and fails loudly instead of skipping, because a green run that tested
/// nothing is worse than a red one.
///
/// WHAT PARITY MEANS. data/NavCellFromMesh.json was produced by the Python lookup over the very same
/// geometry (the exporter re-verifies that the .nav round-trip gives 0 differences), so every spawn
/// must agree. A gap would be a port bug - typically the int32 overflow of the crossing product in
/// <c>contains</c> (products reach ~1e10) or a grid/round difference. The second oracle,
/// data/NpcNavCell.json, holds the cell values sent on the wire for NPC spawns.
/// </summary>
public class NavMeshCellAtTests
{
    private static readonly int[] SampleMaps = { 1100, 1101, 1104 };

    private sealed record Spawn(int MapId, int Index, double PosX, double PosY, double PosZ);

    [Fact]
    public void CellAt_MatchesPythonMeshCells_OnSampleMaps()
    {
        var (ok, total, misses) = ScoreAgainstMeshTable(SampleMaps);

        Assert.True(total > 500, $"sample too small to mean anything: {total} spawns");
        Assert.True(ok >= total * 0.99,
            $"parity {ok}/{total} below 99%; first misses: {string.Join("; ", misses.Take(10))}");
    }

    [Fact]
    public void CellAt_MatchesPythonMeshCells_OnEveryMapWithAnArtifact()
    {
        var (ok, total, misses) = ScoreAgainstMeshTable(null);

        Assert.True(total > 25_000, $"expected the full spawn table, got {total}");
        Assert.True(ok >= total * 0.99,
            $"parity {ok}/{total} below 99%; first misses: {string.Join("; ", misses.Take(10))}");
    }

    [Fact]
    public void CellAt_AgreesWithRetailWireCells_AtThePythonValidatedRate()
    {
        // data/NpcNavCell.json holds the cell values sent on the wire for NPC spawns.
        var navDir = RequireNavDirectory();
        var measured = JsonSerializer.Deserialize<Dictionary<string, int>>(
            File.ReadAllText(Path.Combine(RequireDataDirectory(), "NpcNavCell.json")))!;
        var spawns = LoadSpawns().ToDictionary(s => (s.MapId, s.Index));   // already de-duplicated
        var meshes = new Dictionary<int, NavMesh>();

        int ok = 0, total = 0;
        foreach (var (key, cell) in measured)
        {
            var parts = key.Split(':');
            var map = int.Parse(parts[0]);
            if (!spawns.TryGetValue((map, int.Parse(parts[1])), out var s)) continue;
            if (!meshes.TryGetValue(map, out var mesh))
                meshes[map] = mesh = NavMesh.Load(Path.Combine(navDir, map + ".nav"));
            total++;
            if (mesh.CellAt(s.PosX, s.PosY, s.PosZ) == cell) ok++;
        }

        Assert.True(total > 6_000, $"expected ~6,715 measured rows, joined {total}");
        Assert.True(ok >= total * 0.995, $"wire agreement {ok}/{total} below 99.5%");
    }

    [Fact]
    public void CellAt_NeverLeavesTheMesh_EvenFarOffIt()
    {
        // The client indexes an array with this value unchecked: -1 or >= PolyCount is a crash or (0,0,0).
        var mesh = NavMesh.Load(Path.Combine(RequireNavDirectory(), "1100.nav"));
        foreach (var (x, y) in new[] { (0, 0), (-5_000_000, 7), (int.MaxValue / 2, int.MaxValue / 2), (60_000, 400_000) })
            Assert.InRange(mesh.CellAt(x, y, 0f), 0, mesh.PolyCount - 1);
    }

    [Fact]
    public void CellAt_HandlesCrossingProductsBeyondInt32()
    {
        // For the interior point (20000, 20000) the hypotenuse edge gives
        // side = (20000-0)*(0-100000) - (20000-100000)*(100000-0) = 6e9, past int.MaxValue (2.1e9).
        // With int arithmetic it wraps and the point is classified outside / on the wrong side.
        var mesh = LoadSynthetic(
            new[] { (0, 0), (100_000, 0), (0, 100_000), (200_000, 200_000), (300_000, 200_000), (300_000, 300_000) },
            new[] { new[] { 0, 1, 2 }, new[] { 3, 4, 5 } }, new[] { 0f, 0f });
        Assert.Equal(0, mesh.CellAt(20_000, 20_000, 0f));
        Assert.Equal(1, mesh.CellAt(280_000, 220_000, 0f));
        // Inside the bbox of polygon 1 but outside its ring: nothing contains it, so it is the nearest-bbox fallback.
        Assert.Equal(0, mesh.CellAt(90_000, 90_000, 0f));
    }

    [Fact]
    public void CellAt_PicksTheClosestMeanZ_WhereRingsOverlap()
    {
        // Two stacked squares (a bridge over a road): same xy, different height.
        var sq = new[] { (0, 0), (1000, 0), (1000, 1000), (0, 1000) };
        var mesh = LoadSynthetic(sq, new[] { new[] { 0, 1, 2, 3 }, new[] { 0, 1, 2, 3 } }, new[] { 0f, 500f });
        Assert.Equal(0, mesh.CellAt(500, 500, 10f));
        Assert.Equal(1, mesh.CellAt(500, 500, 480f));
        Assert.Equal(0, mesh.CellAt(500, 500, 250f));   // exact tie -> first polygon, like Python min()
    }

    [Fact]
    public void CellAt_WorldOverload_ScalesBy100()
    {
        var verts = new[] { (0, 0), (1000, 0), (1000, 1000), (0, 1000), (2000, 0), (2000, 1000) };
        var mesh = LoadSynthetic(verts, new[] { new[] { 0, 1, 2, 3 }, new[] { 1, 4, 5, 2 } }, new[] { 0f, 0f });
        Assert.Equal(0, mesh.CellAt(4.0, 4.0, 0.0));    // mesh (400, 400)
        Assert.Equal(1, mesh.CellAt(15.0, 5.0, 0.0));   // mesh (1500, 500)
    }

    [Fact]
    public void Load_RejectsGarbage_AndTryLoadReturnsNull()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ro2-nav-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var bad = Path.Combine(dir, "1.nav");
            using (var gz = new GZipStream(File.Create(bad), CompressionLevel.Fastest))
                gz.Write("not a navmesh"u8);
            Assert.Throws<InvalidDataException>(() => NavMesh.Load(bad));
            Assert.Null(NavMesh.TryLoad(bad));
            Assert.Null(NavMesh.TryLoad(Path.Combine(dir, "missing.nav")));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private static (int Ok, int Total, List<string> Misses) ScoreAgainstMeshTable(int[]? onlyMaps)
    {
        var navDir = RequireNavDirectory();
        var table = JsonSerializer.Deserialize<Dictionary<string, int>>(
            File.ReadAllText(Path.Combine(RequireDataDirectory(), "NavCellFromMesh.json")))!;

        int ok = 0, total = 0;
        var misses = new List<string>();
        var meshes = new Dictionary<int, NavMesh>();
        foreach (var s in LoadSpawns())
        {
            if (onlyMaps is not null && !onlyMaps.Contains(s.MapId)) continue;
            if (!table.TryGetValue($"{s.MapId}:{s.Index}", out var expected)) continue;   // map without ground.tok
            if (!meshes.TryGetValue(s.MapId, out var mesh))
                meshes[s.MapId] = mesh = NavMesh.Load(Path.Combine(navDir, s.MapId + ".nav"));

            total++;
            var got = mesh.CellAt(s.PosX, s.PosY, s.PosZ);
            if (got == expected) ok++;
            else misses.Add($"{s.MapId}:{s.Index} expected {expected} got {got}");
        }
        return (ok, total, misses);
    }

    /// <summary>
    /// Every spawn row, one per (MapId, Index). The table repeats a few keys (e.g. 426102:223); like
    /// the Python <c>load_spawns</c> dict, the LAST row wins, which is the one the oracle was built from.
    /// </summary>
    private static List<Spawn> LoadSpawns()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RequireDataDirectory(), "ZoneSpawn.json")));
        var byKey = new Dictionary<(int, int), Spawn>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var s = new Spawn(e.GetProperty("MapId").GetInt32(), e.GetProperty("Index").GetInt32(),
                e.GetProperty("PosX").GetDouble(), e.GetProperty("PosY").GetDouble(), e.GetProperty("PosZ").GetDouble());
            byKey[(s.MapId, s.Index)] = s;
        }
        return byKey.Values.ToList();
    }

    /// <summary>Builds a NavMesh through the real .nav loader from hand-written geometry.</summary>
    private static NavMesh LoadSynthetic((int X, int Y)[] verts, int[][] rings, float[] meanZ)
    {
        var raw = new MemoryStream();
        using (var gz = new GZipStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        using (var w = new BinaryWriter(gz))
        {
            w.Write("RNAV"u8);
            w.Write((ushort)1);
            w.Write(0);
            w.Write(verts.Length);
            foreach (var (x, y) in verts) { w.Write(x); w.Write(y); }
            w.Write(rings.Length);
            for (var p = 0; p < rings.Length; p++)
            {
                w.Write(meanZ[p]);
                w.Write((ushort)rings[p].Length);
                foreach (var i in rings[p]) w.Write(i);
            }
        }
        raw.Position = 0;
        return NavMesh.Load(raw);
    }

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

    private static string RequireNavDirectory()
    {
        var path = Path.Combine(RequireDataDirectory(), "navmesh");
        Assert.True(Directory.Exists(path) && File.Exists(Path.Combine(path, "1100.nav")),
            "data/navmesh/*.nav is missing - regenerate it with: python tools/export_navmesh.py");
        return path;
    }
}
