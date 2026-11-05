using System.IO.Compression;
using RO2.GameData;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Worlds.Domain;
using Xunit;

namespace RO2.Tests.Validation;

/// <summary>Per-map padded navmesh bounds fed to <see cref="MovementGuard"/>; a map without a navmesh accepts any finite position.</summary>
public class MovementBoundsTests
{
    private sealed class TestClock : IClock
    {
        public DateTime UtcNow { get; set; } = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    }

    // One square polygon spanning 0..1000 world units (0..100000 mesh units).
    private static NavMesh Square()
    {
        var raw = new MemoryStream();
        using (var gz = new GZipStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        using (var w = new BinaryWriter(gz))
        {
            w.Write("RNAV"u8);
            w.Write((ushort)1);
            w.Write(0);
            w.Write(4);
            foreach (var (x, y) in new[] { (0, 0), (100000, 0), (100000, 100000), (0, 100000) }) { w.Write(x); w.Write(y); }
            w.Write(1);
            w.Write(0f);
            w.Write((ushort)4);
            for (var i = 0; i < 4; i++) w.Write(i);
        }
        raw.Position = 0;
        return NavMesh.Load(raw);
    }

    private static MoveRefusal Move(MoveBounds? bounds, float x, float y)
    {
        var guard = new MovementGuard(new TestClock());
        var p = new PlayerState { MapId = 1100, X = 500f, Y = 500f, Z = 0f };
        // Huge speed so the step-length check never fires: these cases isolate the BOUNDS dimension.
        return guard.ValidateClientMove(p, new MovePos(x, y, 0f), 1_000_000f, bounds);
    }

    [Fact]
    public void FarOutsidePaddedExtentIsRefused()
        => Assert.Equal(MoveRefusal.OutOfBounds, Move(MovementGuard.BoundsFor(Square()), 500000f, 500f));

    [Fact]
    public void InsideAndJustPastTheEdgeAreAccepted()
    {
        var b = MovementGuard.BoundsFor(Square());
        Assert.Equal(MoveRefusal.None, Move(b, 500f, 500f));
        Assert.Equal(MoveRefusal.None, Move(b, 1100f, 500f)); // 100 past the mesh edge, inside the padding
    }

    [Fact]
    public void MapWithoutNavMeshHasNoBoundsAndAcceptsAnyFinitePosition()
    {
        Assert.Null(MovementGuard.BoundsFor(null));
        Assert.Equal(MoveRefusal.None, Move(null, 5000f, 500f));
    }
}
