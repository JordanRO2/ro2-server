using System;
using System.Collections.Generic;
using System.Linq;
using RO2.GameData.Tables;
using RO2.Inventory.Domain;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Unit tests for the Honing (RMI 1676) roll and its line storage. Uses hand-built grinding tables so it needs
/// no shipped data; asserts the structural invariants that come straight from the measured retail model
/// (prefix pair shares one even tier below the cap, bonus tiers below the ~0.38*av cap, grv from rr in
/// [-1000,2600], every line a real ability), not the exact RNG.
/// </summary>
public class HoningRollTests
{
    // stats 0..5 at tiers 1..20; Abili_Val ladder = tier*1000 (shared across stats); Normal_Val spans the buckets.
    private static readonly List<GrindingAbili> Abili =
        (from stat in Enumerable.Range(0, 6)
         from tier in Enumerable.Range(1, 20)
         select new GrindingAbili
         {
             Abili_ID = (ushort)(stat * 1000 + tier),
             Abili_Val = (ushort)(tier * 1000),
             Normal_Val = (ushort)(tier * 12),   // tier 1 -> 12 (<30), tier 5 -> 60 (30-99), tier 9+ -> >=100
         }).ToList();

    // 3 families at even tiers 2..20.
    private static readonly List<GrindingPrefixAbili> Prefixes =
        (from fam in new[] { (0, 1), (2, 3), (0, 4) }
         from tier in new[] { 2, 4, 6, 8, 10, 12, 14, 16, 18, 20 }
         select new GrindingPrefixAbili
         {
             Abili_ID_1 = (ushort)(fam.Item1 * 1000 + tier),
             Abili_ID_2 = (ushort)(fam.Item2 * 1000 + tier),
         }).ToList();

    private static readonly HashSet<ushort> ValidIds = Abili.Select(a => a.Abili_ID).ToHashSet();

    [Fact]
    public void Roll_ProducesValidHonedStructure_OverManySeeds()
    {
        const uint av = 20000;                 // prefix cap tier 20, bonus cap tier 7 (0.38*20000=7600)
        var rng = new Random(12345);
        int fourLine = 0, fiveLine = 0;
        long prefixSum = 0, bonusSum = 0; int prefixN = 0, bonusN = 0;

        for (int iter = 0; iter < 2000; iter++)
        {
            var r = HoningRoll.Roll(av, Abili, Prefixes, rng);
            Assert.NotNull(r);
            var lines = r!.Value.Lines;
            Assert.InRange(lines.Count, 4, 5);
            if (lines.Count == 4) fourLine++; else fiveLine++;

            // prefix pair: two lines, same (even) tier, below the prefix cap, valid ids
            Assert.Equal(lines[0].Tier, lines[1].Tier);
            Assert.True(lines[0].Tier % 2 == 0 && lines[0].Tier is >= 2 and <= 20);

            // grv from rr in [-1000, 2600]
            Assert.InRange(r.Value.Grv, (uint)(av * 0.9), (uint)(av * 1.27));

            for (int i = 0; i < lines.Count; i++)
            {
                Assert.Contains(lines[i].AbiliId, ValidIds);
                if (i >= 2) Assert.True(lines[i].Tier is >= 1 and <= 7, $"bonus tier {lines[i].Tier} above cap 7");
                if (i < 2) { prefixSum += lines[i].Roll; prefixN++; }
                else { bonusSum += lines[i].Roll; bonusN++; }
            }
        }

        Assert.True(fourLine > 0, "some items should drop the 5th line (~12%)");
        Assert.True(fiveLine > fourLine, "most items should carry 5 lines");
        // the measured signature: prefix lines roll markedly more negative than bonus lines
        double prefixMean = (double)prefixSum / prefixN, bonusMean = (double)bonusSum / bonusN;
        Assert.True(prefixMean < bonusMean, $"prefix mean {prefixMean:F0} should be below bonus mean {bonusMean:F0}");
    }

    [Fact]
    public void Roll_ReturnsNull_WhenItemTooLowToHone()
    {
        // av below the lowest tier's Abili_Val (1000) -> no eligible tier.
        Assert.Null(HoningRoll.Roll(500, Abili, Prefixes, new Random(1)));
        Assert.Null(HoningRoll.Roll(20000, new List<GrindingAbili>(), Prefixes, new Random(1)));
    }

    [Fact]
    public void GrindingLines_RoundTrips()
    {
        var lines = new List<GrindingLine>
        {
            new(4020, -1163), new(20, -1243), new(7009, 159), new(14013, -713),
        };
        var json = GrindingLines.Serialize(lines);
        Assert.NotNull(json);
        var back = GrindingLines.Parse(json);
        Assert.Equal(lines.Count, back.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            Assert.Equal(lines[i].AbiliId, back[i].AbiliId);
            Assert.Equal(lines[i].Roll, back[i].Roll);
        }
        Assert.Null(GrindingLines.Serialize(new List<GrindingLine>()));
        Assert.Empty(GrindingLines.Parse(null));
        Assert.Empty(GrindingLines.Parse("not json"));
    }
}
