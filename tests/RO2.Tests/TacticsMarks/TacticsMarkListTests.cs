using RO2.TacticsMarks.Domain;
using Xunit;

namespace RO2.Tests.TacticsMarks;

public class TacticsMarkListTests
{
    private static readonly DateTime T0 = new(2026, 8, 9, 21, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ANumberedMark_MovesToTheNewTarget_AndTheTargetLeavesItsOtherEntry()
    {
        var list = new TacticsMarkList();
        list.Set(0xA1, 12, false, T0);
        list.Set(0xB2, 5, true, T0.AddSeconds(1));
        list.Set(0xA1, 5, true, T0.AddSeconds(2));
        Assert.Equal(new[] { new TacticsMark(5, 0xA1, T0.AddSeconds(2)) }, list.Snapshot());
    }

    [Fact]
    public void APictureMark_ReplacesTheTargetsEntry_AndAppendsLast()
    {
        var list = new TacticsMarkList();
        list.Set(0xA1, 13, false, T0);
        list.Set(0xB2, 13, false, T0.AddSeconds(1));
        list.Set(0xA1, 14, false, T0.AddSeconds(2));
        Assert.Equal(new ushort[] { 13, 14 }, list.Snapshot().Select(e => e.Mark));
        Assert.Equal(new uint[] { 0xB2, 0xA1 }, list.Snapshot().Select(e => e.Handle));
    }

    [Fact]
    public void Snapshot_WithACutoff_KeepsOnlyTheMarksSetStrictlyAfter()
    {
        var list = new TacticsMarkList();
        list.Set(1, 13, false, T0);
        list.Set(2, 14, false, T0.AddSeconds(5));
        Assert.Equal(new uint[] { 2 }, list.Snapshot(T0).Select(e => e.Handle));
        Assert.Empty(list.Snapshot(T0.AddSeconds(5)));
    }
}
