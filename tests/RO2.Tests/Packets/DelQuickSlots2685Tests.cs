using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>Byte gate for RMI 2685 NfyChangeProJobDelQuickSlot, the bar cells emptied by a skill reset.</summary>
/// <remarks>
/// Covers the three distinct bodies sent in answer to a 1323 skill-reset scroll. The expected cell lists
/// are the skill entries of each bar before the reset, so the test does not derive its expectation from
/// the frame it checks.
/// </remarks>
public class DelQuickSlots2685Tests
{
    [Fact]
    public void ReproducesTheThreeCellThiefReset()
    {
        
        var body = JobChangePackets.BuildDelQuickSlots2685(new (byte, byte)[] { (0, 1), (0, 2), (0, 3) });
        Assert.Equal(Convert.FromHexString("080000000300000100020003"), body);
    }

    [Fact]
    public void ReproducesTheTwelveCellSoulMakerReset()
    {
        
        var cells = new (byte, byte)[]
        {
            (0, 0), (0, 1), (0, 2), (0, 3), (0, 4), (0, 5), (0, 6), (0, 7), (1, 0), (1, 1), (1, 2), (2, 2),
        };
        var body = JobChangePackets.BuildDelQuickSlots2685(cells);
        Assert.Equal(Convert.FromHexString("1a0000000c00000000010002000300040005000600070100010101020202"), body);
    }

    [Fact]
    public void ReproducesTheTwentyTwoCellWarriorReset()
    {
        
        var cells = new (byte, byte)[]
        {
            (0, 0), (0, 1), (0, 2), (0, 3), (0, 4), (0, 5), (0, 6), (0, 7), (0, 9),
            (1, 0), (1, 1), (1, 2), (1, 3), (1, 5), (1, 6), (1, 8), (1, 9),
            (2, 0), (2, 1), (2, 2), (2, 8), (2, 9),
        };
        var body = JobChangePackets.BuildDelQuickSlots2685(cells);
        Assert.Equal(Convert.FromHexString(
            "2e00000016000000000100020003000400050006000700090100010101020103010501060108010902000201020202080209"),
            body);
    }
}
