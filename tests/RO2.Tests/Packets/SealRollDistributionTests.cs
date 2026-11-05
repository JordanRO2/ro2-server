using RO2.Inventory.Domain;
using Xunit;
using Xunit.Abstractions;

namespace RO2.Tests.Packets;

/// <summary>Measures the seal roll distribution: centered on 0, symmetric, and WEIGHTED toward the low end (a near-cap
/// value is rare), so it does not read as "always maxing".</summary>
public class SealRollDistributionTests(ITestOutputHelper output)
{
    [Fact]
    public void Roll_is_weighted_toward_low()
    {
        const int cap = 60;   // VIT
        var cand = new[] { new SealRoll.Candidate(1, 4, -cap, cap) };
        var rng = new Random(12345);
        const int n = 100_000;
        long sum = 0; int atMax = 0, negative = 0, lowHalf = 0;
        var buckets = new int[12];   // 10% bands of |v|/cap, plus overflow
        for (int i = 0; i < n; i++)
        {
            int v = SealRoll.Roll(cand, rng)!.Value.Value;
            sum += v;
            if (v == cap || v == -cap) atMax++;
            if (v < 0) negative++;
            if (Math.Abs(v) <= cap / 2) lowHalf++;
            buckets[Math.Min(11, Math.Abs(v) * 10 / cap)]++;
        }
        double mean = (double)sum / n;
        output.WriteLine($"mean={mean:F2} (expect ~0)  negative={100.0*negative/n:F1}% (expect ~50)  |v|<=cap/2={100.0*lowHalf/n:F1}% (expect ~50)  atExactMax={100.0*atMax/n:F2}%");
        for (int b = 0; b < 11; b++) output.WriteLine($"|v|/cap {b*10}-{b*10+10}%: {100.0*buckets[b]/n:F1}%");

        Assert.InRange(mean, -2.0, 2.0);                 // centered on 0
        Assert.InRange(100.0 * negative / n, 48, 52);    // still symmetric: ~half negative
        Assert.InRange(100.0 * lowHalf / n, 70, 80);     // triangular -> ~75% within cap/2 (vs 50% uniform)
        Assert.True(100.0 * atMax / n < 0.2, "near-cap must be rare");
    }
}
