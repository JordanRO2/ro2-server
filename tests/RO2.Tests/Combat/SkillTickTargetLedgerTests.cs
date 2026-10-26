using System;
using System.Linq;
using RO2.Combats.Domain;
using Xunit;

namespace RO2.Tests.Combat;

/// <summary>Characterization of <see cref="SkillTickTargetLedger"/> (the 2718 target record moved out of the host in B4 S13).</summary>
public class SkillTickTargetLedgerTests
{
    private const uint Map = 1300;

    [Fact]
    public void Record_DropsZeroAndRepeats_KeepsOrder()
    {
        var ledger = new SkillTickTargetLedger();
        ledger.Record(Map, 210706, new uint[] { 0x000F5413, 0, 0x000F5213, 0x000F5413 });
        Assert.Equal(new uint[] { 0x000F5413, 0x000F5213 }, ledger.Recorded(Map, 210706).ToArray());
    }

    [Fact]
    public void Record_ReplacesThePriorCastOfTheSameSkill()
    {
        var ledger = new SkillTickTargetLedger();
        ledger.Record(Map, 210706, new uint[] { 0x000F5413, 0x000F5213 });
        ledger.Record(Map, 210706, new uint[] { 0x000F5813 });
        Assert.Equal(new uint[] { 0x000F5813 }, ledger.Recorded(Map, 210706).ToArray());
    }

    [Fact]
    public void Recorded_EmptyForOtherSkillOrOtherMap()
    {
        var ledger = new SkillTickTargetLedger();
        ledger.Record(Map, 210706, new uint[] { 0x000F5413 });
        Assert.Empty(ledger.Recorded(Map, 210705));     // another skill
        Assert.Empty(ledger.Recorded(Map + 1, 210706)); // another map
    }

    [Fact]
    public void Record_EmptyHandles_ClearsToEmpty()
    {
        var ledger = new SkillTickTargetLedger();
        ledger.Record(Map, 210706, new uint[] { 0x000F5413 });
        ledger.Record(Map, 210706, Array.Empty<uint>());
        Assert.Empty(ledger.Recorded(Map, 210706));
    }
}
