using System.Buffers.Binary;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Services;

/// <summary>
/// The two-cell 2318 overload the chase uses so a moving monster's origin and destination carry their OWN navmesh cells (the position→cell solver).
/// Pure: no sockets, no game data.
/// </summary>
public class NpcChaseTests
{
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
