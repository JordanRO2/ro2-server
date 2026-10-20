using System.IO.Compression;

namespace RO2.Domain.GameData;

/// <summary>
/// Runtime point -> navmesh-cell solver for ONE map: the polygon set of the client's own PathEngine
/// navmesh (BACKGROUND/ZONE/&lt;map&gt;/navidata/ground.tok), loaded from the compact artifact
/// data/navmesh/&lt;mapId&gt;.nav that tools/export_navmesh.py writes.
///
/// WHY. RMI 2318 / 2506 carry a navmesh CELL INDEX, not a Z: the client feeds it to PathEngine's
/// cMesh::heightAtPositionF as an unchecked array subscript, so a wrong cell is a wrong height (the
/// actor skates) or a client crash. data/NpcNavCell.json and data/NavCellFromMesh.json only know the
/// cell of a SPAWN point; a moving NPC needs the cell of an ARBITRARY destination, which is this class.
///
/// ALGORITHM. A faithful port of <c>NavMesh.cell_at</c> in tools/parse_ground_tok.py, which is validated
/// against the anchors in the .tok files and against the known cells in NpcNavCell.json.
/// The cell is the index of the polygon whose 2D ring contains the point; where several do
/// (bridges, ramps, upper floors) the one whose mean vertex z is closest to the point's z wins; an
/// off-mesh point falls back to the polygon whose bounding box is nearest. xy stays in exact integer
/// arithmetic - the crossing products reach ~1e10, so they are <see cref="long"/>, never int (the
/// Python reference has bignums and would hide an overflow here).
///
/// .nav BYTE LAYOUT (version 1) - must stay identical to the writer in tools/export_navmesh.py.
/// The file is one GZip stream; the decompressed payload is little-endian:
/// <code>
///   0   4      magic      ASCII "RNAV"
///   4   2      version    uint16 = 1
///   6   4      mapId      int32
///   10  4      vertCount  int32 = V
///   14  8*V    verts      V x { int32 x, int32 y }      (world * 100 = mesh units)
///   ..  4      polyCount  int32 = P
///   ..  var    polys      P x { float32 meanZ,          (mean vertex z, mesh units)
///                               uint16  ringLen = L,    (&gt;= 3)
///                               int32   ring[L] }       (indices into verts, in ring order)
/// </code>
/// A polygon's position in <c>polys</c> IS its cell index. Bounding boxes, the map extents and the
/// <see cref="GridCells"/> bucket grid are derived on load, exactly as the Python builds them.
/// </summary>
public sealed class NavMesh
{
    /// <summary>Buckets per axis of the point-lookup grid. Purely an optimisation; matches GRID_CELLS in Python.</summary>
    public const int GridCells = 96;

    /// <summary>Mesh units per world unit: the mesh is stored in the same space as the wire (world * 100).</summary>
    public const double WorldToMesh = 100.0;

    private const uint Magic = 0x56414E52;   // "RNAV" read as little-endian uint32
    private const ushort SupportedVersion = 1;
    private const int MinRingVertices = 3;   // a polygon ring needs at least a triangle

    private readonly int[] _vx;
    private readonly int[] _vy;
    private readonly int[][] _rings;
    private readonly double[] _polyZ;        // float32 on disk, widened: Python also compares in double
    private readonly int[] _bx0, _by0, _bx1, _by1;
    private readonly int _minX, _minY, _maxX, _maxY, _cellW, _cellH, _gridW, _gridH;
    private readonly int[]?[] _grid;         // gridW * gridH buckets of ascending polygon indices

    /// <summary>The map this mesh belongs to, as recorded in the artifact.</summary>
    public int MapId { get; }

    /// <summary>Number of polygons, i.e. the exclusive upper bound of every cell this mesh can return.</summary>
    public int PolyCount => _rings.Length;

    private NavMesh(int mapId, int[] vx, int[] vy, int[][] rings, double[] polyZ)
    {
        MapId = mapId;
        _vx = vx;
        _vy = vy;
        _rings = rings;
        _polyZ = polyZ;

        var n = rings.Length;
        _bx0 = new int[n]; _by0 = new int[n]; _bx1 = new int[n]; _by1 = new int[n];
        _minX = int.MaxValue; _minY = int.MaxValue; _maxX = int.MinValue; _maxY = int.MinValue;
        for (var p = 0; p < n; p++)
        {
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
            foreach (var v in rings[p])
            {
                x0 = Math.Min(x0, vx[v]); x1 = Math.Max(x1, vx[v]);
                y0 = Math.Min(y0, vy[v]); y1 = Math.Max(y1, vy[v]);
            }
            _bx0[p] = x0; _by0[p] = y0; _bx1[p] = x1; _by1[p] = y1;
            _minX = Math.Min(_minX, x0); _maxX = Math.Max(_maxX, x1);
            _minY = Math.Min(_minY, y0); _maxY = Math.Max(_maxY, y1);
        }

        // Same integer arithmetic as Python: max(1, span // GRID_CELLS + 1) (all operands non-negative).
        _cellW = Math.Max(1, (_maxX - _minX) / GridCells + 1);
        _cellH = Math.Max(1, (_maxY - _minY) / GridCells + 1);
        _gridW = (_maxX - _minX) / _cellW + 1;
        _gridH = (_maxY - _minY) / _cellH + 1;

        var buckets = new List<int>?[_gridW * _gridH];
        for (var p = 0; p < n; p++)
        {
            for (var gx = (_bx0[p] - _minX) / _cellW; gx <= (_bx1[p] - _minX) / _cellW; gx++)
            {
                for (var gy = (_by0[p] - _minY) / _cellH; gy <= (_by1[p] - _minY) / _cellH; gy++)
                    (buckets[gx * _gridH + gy] ??= new List<int>()).Add(p);
            }
        }
        _grid = new int[]?[buckets.Length];
        for (var i = 0; i < buckets.Length; i++) _grid[i] = buckets[i]?.ToArray();
    }

    // ---------------------------------------------------------------------
    // Lookup
    // ---------------------------------------------------------------------

    /// <summary>
    /// Navmesh cell for a point in MESH units (world * 100). <paramref name="zMesh"/> only breaks ties
    /// between overlapping polygons. Always returns a valid index in [0, <see cref="PolyCount"/>):
    /// an off-mesh point resolves to the polygon with the nearest bounding box, never to -1.
    /// </summary>
    public int CellAt(int xMesh, int yMesh, float zMesh)
    {
        if (xMesh < _minX || xMesh > _maxX || yMesh < _minY || yMesh > _maxY)
            return Nearest(xMesh, yMesh);

        var bucket = _grid[(xMesh - _minX) / _cellW * _gridH + (yMesh - _minY) / _cellH];
        var best = -1;
        var bestDz = 0.0;
        if (bucket is not null)
        {
            foreach (var p in bucket)
            {
                if (xMesh < _bx0[p] || xMesh > _bx1[p] || yMesh < _by0[p] || yMesh > _by1[p]) continue;
                if (!Contains(xMesh, yMesh, _rings[p])) continue;

                // Unique hit wins outright; several -> closest mean-z, first one on a tie (Python min()).
                var dz = Math.Abs(_polyZ[p] - zMesh);
                if (best < 0 || dz < bestDz) { best = p; bestDz = dz; }
            }
        }
        return best >= 0 ? best : Nearest(xMesh, yMesh);
    }

    /// <summary>
    /// Navmesh cell for a point in WORLD units: <c>round(world * 100)</c> to mesh units, then
    /// <see cref="CellAt(int, int, float)"/>. z is scaled but not rounded (it is only a tiebreak).
    ///
    /// Rounding nuance. The WIRE carries trunc(pos * 100) (ToWireCoord), but tools/parse_ground_tok.py
    /// validated this lookup with round(pos * 100) (<c>to_mesh</c>), and NavCellFromMesh.json was
    /// generated that way, so this keeps round - ties go to even, like Python's round(). The two differ
    /// by at most one mesh unit (1 cm), which only matters for a point lying within 1 cm of a polygon edge.
    /// </summary>
    public int CellAt(double worldX, double worldY, double worldZ) =>
        CellAt((int)Math.Round(worldX * WorldToMesh, MidpointRounding.ToEven),
               (int)Math.Round(worldY * WorldToMesh, MidpointRounding.ToEven),
               (float)(worldZ * WorldToMesh));

    /// <summary>
    /// Crossing-number point-in-polygon, ported verbatim from <c>NavMesh.contains</c>:
    /// an edge counts when it straddles y, and the point is on the crossing side when
    /// <c>(side &gt; 0) == (y2 &gt; y1)</c> with <c>side = (y-y1)*(x2-x1) - (x-x1)*(y2-y1)</c>.
    /// The products reach ~1e10 (x,y up to ~130000), so they MUST be long.
    /// </summary>
    private bool Contains(int x, int y, int[] ring)
    {
        var inside = false;
        var n = ring.Length;
        for (var i = 0; i < n; i++)
        {
            long x1 = _vx[ring[i]], y1 = _vy[ring[i]];
            long x2 = _vx[ring[i + 1 < n ? i + 1 : 0]], y2 = _vy[ring[i + 1 < n ? i + 1 : 0]];
            if ((y1 > y) != (y2 > y))
            {
                var side = (y - y1) * (x2 - x1) - (x - x1) * (y2 - y1);
                if ((side > 0) == (y2 > y1)) inside = !inside;
            }
        }
        return inside;
    }

    /// <summary>Closest polygon by squared bounding-box distance (lowest index on a tie). Off-mesh only.</summary>
    private int Nearest(int x, int y)
    {
        var best = 0;
        var bestDist = long.MaxValue;
        for (var p = 0; p < _rings.Length; p++)
        {
            long dx = x >= _bx0[p] && x <= _bx1[p] ? 0 : x < _bx0[p] ? _bx0[p] - x : x - _bx1[p];
            long dy = y >= _by0[p] && y <= _by1[p] ? 0 : y < _by0[p] ? _by0[p] - y : y - _by1[p];
            var dist = dx * dx + dy * dy;
            if (dist < bestDist) { best = p; bestDist = dist; }
        }
        return best;
    }

    // ---------------------------------------------------------------------
    // Loading
    // ---------------------------------------------------------------------

    /// <summary>Loads one map's GZip-compressed .nav file. Throws <see cref="InvalidDataException"/> if it is malformed.</summary>
    public static NavMesh Load(string path)
    {
        using var file = File.OpenRead(path);
        return Load(file);
    }

    /// <summary>Loads from a stream holding the GZip-compressed .nav payload.</summary>
    public static NavMesh Load(Stream gzipped)
    {
        try
        {
            using var gz = new GZipStream(gzipped, CompressionMode.Decompress, leaveOpen: true);
            using var r = new BinaryReader(gz);

            if (r.ReadUInt32() != Magic) throw new InvalidDataException("not a .nav file (bad magic)");
            var version = r.ReadUInt16();
            if (version != SupportedVersion) throw new InvalidDataException($"unsupported .nav version {version}");
            var mapId = r.ReadInt32();

            var vertCount = r.ReadInt32();
            if (vertCount < 0) throw new InvalidDataException("negative vertex count");
            var vx = new int[vertCount];
            var vy = new int[vertCount];
            for (var i = 0; i < vertCount; i++) { vx[i] = r.ReadInt32(); vy[i] = r.ReadInt32(); }

            var polyCount = r.ReadInt32();
            if (polyCount <= 0) throw new InvalidDataException("a navmesh needs at least one polygon");
            var rings = new int[polyCount][];
            var polyZ = new double[polyCount];
            for (var p = 0; p < polyCount; p++)
            {
                polyZ[p] = r.ReadSingle();
                var len = r.ReadUInt16();
                if (len < MinRingVertices) throw new InvalidDataException($"polygon {p} has {len} vertices");
                var ring = new int[len];
                for (var i = 0; i < len; i++)
                {
                    ring[i] = r.ReadInt32();
                    if ((uint)ring[i] >= (uint)vertCount)
                        throw new InvalidDataException($"polygon {p} references vertex {ring[i]} of {vertCount}");
                }
                rings[p] = ring;
            }
            if (gz.ReadByte() != -1) throw new InvalidDataException("trailing bytes after the last polygon");
            return new NavMesh(mapId, vx, vy, rings, polyZ);
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidDataException("truncated .nav file", e);
        }
    }

    /// <summary>Like <see cref="Load(string)"/> but returns null when the file is missing or unreadable, so a map without a mesh degrades instead of failing startup.</summary>
    public static NavMesh? TryLoad(string path)
    {
        if (!File.Exists(path)) return null;
        try { return Load(path); }
        catch (Exception e) when (e is InvalidDataException or IOException) { return null; }
    }
}
