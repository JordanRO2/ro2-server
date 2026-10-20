using System.Buffers.Binary;
using RO2.Domain.GameData;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>Byte-layout gate for RMI 2318 OnMoveNPC.</summary>
/// <remarks>
/// The fixture is a single NPC step with origin and destination in the same cell and speed 1.0:
/// <code>
/// 01 28 | 28 00 | 9b e2 | 00000000 | 13600100 | 421f0100 | c3140100 | 8c040000
/// | 801d0100 | 7f140100 | 8c040000 | 0000803f
/// </code>
/// The NPC handle has low byte 0x13 (NPC). Origin (735.38, 708.51) and destination (730.88, 707.83) are both in
/// cell 1164, a 4.55-unit step.
/// </remarks>
public class OnMoveNpc2318Tests
{
    
    private const string RetailBodyHex =
        "012828009be20000000013600100421f0100c31401008c040000801d01007f1401008c0400000000803f";

    private static (byte[] Body, int Prefix) Build(uint handle, float ox, float oy,
        float dx, float dy, int? cell, float speed)
    {
        var body = NpcSpawnService.BuildOnMoveNpc2318(handle, ox, oy, dx, dy, cell, speed);
        return (body, 1 + body[0]);
    }

    [Fact]
    public void ReproducesARealRetailFrameByteForByte()
    {
        var expected = Convert.FromHexString(RetailBodyHex);
        
        
        
        
        
        
        var (body, _) = Build(0x00016013u, 735.3801f, 708.5101f, 730.8801f, 707.8301f, 1164, 1.0f);

        Assert.Equal(NpcSpawnService.MoveNpcBodyLen, body.Length);
        Assert.Equal(expected.Length, body.Length);
        var differing = Enumerable.Range(0, expected.Length)
            .Where(i => body[i] != expected[i]).ToArray();
        Assert.Equal(Array.Empty<int>(), differing);
    }

    [Fact]
    public void BodyIsAlways42Bytes()
    {
        
        foreach (var cell in new int?[] { null, 0, 1, 1164, 2382, -1, 99999 })
        {
            var (body, _) = Build(0x00016013u, 1.0f, 2.0f, 3.0f, 4.0f, cell, 1.0f);
            Assert.Equal(NpcSpawnService.MoveNpcBodyLen, body.Length);
        }
    }

    [Fact]
    public void NavCellIsNeverOutsideRetailsRange()
    {
        
        
        
        
        
        foreach (var hostile in new int?[] { null, -1, -5000, 2383, 8711, int.MaxValue })
        {
            var (body, prefix) = Build(0x00016013u, 10f, 20f, 11f, 21f, hostile, 1.0f);
            var blob = body.AsSpan(prefix);
            var originCell = BinaryPrimitives.ReadInt32LittleEndian(blob.Slice(20));
            var destCell = BinaryPrimitives.ReadInt32LittleEndian(blob.Slice(32));
            Assert.InRange(originCell, 0, GameDataStore.MaxKnownNavCell);
            Assert.InRange(destCell, 0, GameDataStore.MaxKnownNavCell);
        }
    }

    [Fact]
    public void PositionsUseTruncatedHundredths_NotFloats()
    {
        
        
        var (body, prefix) = Build(1u, 447.525635f, 321.107849f, 448.0f, 322.0f, 5, 1.0f);
        var blob = body.AsSpan(prefix);
        Assert.Equal(44752, BinaryPrimitives.ReadInt32LittleEndian(blob.Slice(12)));
        Assert.Equal(32110, BinaryPrimitives.ReadInt32LittleEndian(blob.Slice(16)));
        Assert.Equal(44800, BinaryPrimitives.ReadInt32LittleEndian(blob.Slice(24)));
        Assert.Equal(32200, BinaryPrimitives.ReadInt32LittleEndian(blob.Slice(28)));
    }

    [Fact]
    public void WanderStaysInsideHoverRadiusAndStepsShort()
    {
        
        
        var npc = new NpcRuntimeState
        {
            EntityOid = 0x113u,
            MapId = 1100u,
            PosX = 100f,
            PosY = 200f,
            NavCell = 42,
            Spawn = new SpawnEntry
            {
                MapId = 1100u, Index = 1, AssetIndex = 10330000u,
                PosX = 100f, PosY = 200f, HoverRadius = 5f,
            },
        };

        var rng = new Random(1234);
        var x = npc.PosX;
        var y = npc.PosY;
        for (var i = 0; i < 5000; i++)
        {
            var (dx, dy) = NpcMovementService.PickDestination(rng, npc);

            var step = MathF.Sqrt((dx - x) * (dx - x) + (dy - y) * (dy - y));
            Assert.True(step <= NpcSpawnService.MaxWanderStep + 0.001f,
                $"step {step} exceeded the cap on iteration {i}");

            var fromSpawn = MathF.Sqrt((dx - 100f) * (dx - 100f) + (dy - 200f) * (dy - 200f));
            Assert.True(fromSpawn <= 5f + 0.001f,
                $"wandered {fromSpawn} from spawn on iteration {i}, outside HoverRadius");

            npc.PosX = x = dx;
            npc.PosY = y = dy;
        }
    }

    [Fact]
    public void FixturesDoNotWander()
    {
        
        
        var npc = new NpcRuntimeState
        {
            PosX = 50f, PosY = 60f,
            Spawn = new SpawnEntry { MapId = 1100u, Index = 2, PosX = 50f, PosY = 60f, HoverRadius = 0f },
        };
        var (dx, dy) = NpcMovementService.PickDestination(new Random(7), npc);
        Assert.Equal(50f, dx, 3);
        Assert.Equal(60f, dy, 3);
    }
}
