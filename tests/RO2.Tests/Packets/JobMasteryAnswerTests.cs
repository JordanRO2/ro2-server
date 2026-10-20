using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Job-mastery answers 2987, 2988 and 2989 (<see cref="JobMasteryPackets"/>). The 2988 shape is pinned to reference data. 2987 and 2989 are pinned to the layout the client's shared reader expects: a u16 result and a u32 NPC handle behind a u32 length.
/// </summary>
public sealed class JobMasteryAnswerTests
{
    private static string ToHex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    [Fact]
    public void Answer_IsTheRetail2988Frame()
    {
        
        
        Assert.Equal("06000000000013a92100", ToHex(JobMasteryPackets.BuildAnswer(0, 0x0021A913)));
        Assert.Equal("06000000000013a82100", ToHex(JobMasteryPackets.BuildAnswer(0, 0x0021A813)));
    }

    [Fact]
    public void TheNeutralAnswers_CarryTheReadersFailureCodes()
    {
        
        Assert.Equal("06000000" + "315c" + "13a92100",   
            ToHex(JobMasteryPackets.BuildAnswer(JobMasteryPackets.StartFailed, 0x0021A913)));
        Assert.Equal("06000000" + "fa5c" + "13a92100",   
            ToHex(JobMasteryPackets.BuildAnswer(JobMasteryPackets.TrainingConditionNotMet, 0x0021A913)));
        Assert.Equal("06000000" + "955c" + "13a92100",   
            ToHex(JobMasteryPackets.BuildAnswer(JobMasteryPackets.UpgradeConditionNotMet, 0x0021A913)));
        Assert.Equal((ushort)23601, JobMasteryPackets.StartFailed);
        Assert.Equal((ushort)23802, JobMasteryPackets.TrainingConditionNotMet);
        Assert.Equal((ushort)23701, JobMasteryPackets.UpgradeConditionNotMet);
    }

    [Fact]
    public void Request_IsTheNpcHandle()
    {
        
        Assert.True(JobMasteryPackets.TryReadNpcHandle(Convert.FromHexString("13a92100"), out var handle));
        Assert.Equal(0x0021A913u, handle);
        Assert.False(JobMasteryPackets.TryReadNpcHandle(Convert.FromHexString("13a921"), out _));
    }
}
