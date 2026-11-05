using RO2.Characters.Domain;
using RO2.Domain.Entities;
using RO2.Server.Zone.Models;
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
        // 995c = 23705: the 2989 body retail sends on an upgrade refusal (06000000 995c 13a92100); pins the real
        // frame, not the earlier 23701 guess.
        Assert.Equal("06000000" + "995c" + "13a92100",
            ToHex(JobMasteryPackets.BuildAnswer(JobMasteryPackets.UpgradeConditionNotMet, 0x0021A913)));
        Assert.Equal((ushort)23601, JobMasteryPackets.StartFailed);
        Assert.Equal((ushort)23802, JobMasteryPackets.TrainingConditionNotMet);
        Assert.Equal((ushort)23705, JobMasteryPackets.UpgradeConditionNotMet);
    }

    [Fact]
    public void Request_IsTheNpcHandle()
    {
        
        Assert.True(JobMasteryPackets.TryReadNpcHandle(Convert.FromHexString("13a92100"), out var handle));
        Assert.Equal(0x0021A913u, handle);
        Assert.False(JobMasteryPackets.TryReadNpcHandle(Convert.FromHexString("13a921"), out _));
    }

    // The mastery handlers take their coin with PlayerState.TryDebit before touching any point, so a short wallet
    // refuses with nothing spent and a funded one is debited once, in lock-step with the persisted Character.
    [Fact]
    public void MasteryCoin_IsTakenAtomically_RefusedWhenShort()
    {
        var p = new PlayerState { Character = new Character { Id = 1, Name = "M", Level = 50 } };
        p.Gold = 100;

        Assert.False(p.TryDebit(101));
        Assert.Equal(100, p.Gold);
        Assert.Equal(100, p.Character!.Gold);

        Assert.True(p.TryDebit(60));
        Assert.Equal(40, p.Gold);
        Assert.Equal(40, p.Character.Gold);
    }
}
