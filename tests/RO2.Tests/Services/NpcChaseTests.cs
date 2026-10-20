using System.Buffers.Binary;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Services;

/// <summary>
/// The chase step geometry (<see cref="NpcChaseService.StepToward"/>) and the two-cell 2318 overload the chase
/// uses so a moving monster's origin and destination carry their OWN navmesh cells (the position→cell solver).
/// Pure: no sockets, no game data.
/// </summary>
public class NpcChaseTests
{
    [Fact]
    public void StepToward_PartialStep_MovesAlongTheLine()
    {
        var (x, y) = NpcChaseService.StepToward(0f, 0f, 3f, 4f, 2.5f);   // dist 5, half way
        Assert.Equal(1.5f, x, 3);
        Assert.Equal(2.0f, y, 3);
    }

    [Fact]
    public void StepToward_StepPastTarget_StopsAtTarget()
    {
        var (x, y) = NpcChaseService.StepToward(0f, 0f, 3f, 4f, 100f);   // step >> dist: clamp to the target
        Assert.Equal(3f, x, 3);
        Assert.Equal(4f, y, 3);
    }

    [Fact]
    public void StepToward_Coincident_ReturnsOrigin()
    {
        var (x, y) = NpcChaseService.StepToward(7f, 9f, 7f, 9f, 3f);
        Assert.Equal(7f, x, 3);
        Assert.Equal(9f, y, 3);
    }

    [Fact]
    public void BuildOnMoveNpc2318_TwoCellOverload_WritesOriginAndDestCellsSeparately()
    {
        // Origin cell 100, destination cell 200: a chase step crosses cells, so they must differ on the wire.
        var body = NpcSpawnService.BuildOnMoveNpc2318(0x00016013u, 10f, 20f, 100, 11f, 21f, 200, 1.0f);
        var blob = body.AsSpan(1 + body[0]);
        var originCell = BinaryPrimitives.ReadInt32LittleEndian(blob.Slice(20));
        var destCell = BinaryPrimitives.ReadInt32LittleEndian(blob.Slice(32));
        Assert.Equal(100, originCell);
        Assert.Equal(200, destCell);
        Assert.Equal(NpcSpawnService.MoveNpcBodyLen, body.Length);
    }

    [Fact]
    public void BuildOnMoveNpc2318_SingleCellOverload_StillReusesOneCellForBoth()
    {
        var body = NpcSpawnService.BuildOnMoveNpc2318(0x00016013u, 10f, 20f, 11f, 21f, 1164, 1.0f);
        var blob = body.AsSpan(1 + body[0]);
        Assert.Equal(1164, BinaryPrimitives.ReadInt32LittleEndian(blob.Slice(20)));
        Assert.Equal(1164, BinaryPrimitives.ReadInt32LittleEndian(blob.Slice(32)));
    }
}
