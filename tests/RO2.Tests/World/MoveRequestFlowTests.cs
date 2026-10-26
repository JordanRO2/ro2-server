using RO2.Worlds.Application;
using RO2.Worlds.Domain;
using Xunit;

namespace RO2.Tests.World;

public class MoveRequestFlowTests
{
    [Theory]
    [InlineData(true, true, false, MoveRequestFlow.Admission.Proceed)]
    [InlineData(false, true, false, MoveRequestFlow.Admission.Drop)]   // malformed length
    [InlineData(true, false, false, MoveRequestFlow.Admission.Drop)]   // map not acked (1304)
    [InlineData(true, true, true, MoveRequestFlow.Admission.Drop)]     // glider re-entry window
    [InlineData(false, false, true, MoveRequestFlow.Admission.Drop)]
    public void Admit_OnlyACanonicalFrameFromALoadedPcOutsideAGliderReentry(
        bool canonical, bool mapLoaded, bool glider, MoveRequestFlow.Admission expected)
        => Assert.Equal(expected, MoveRequestFlow.Admit(canonical, mapLoaded, glider));

    [Theory]
    [InlineData(false, false, MoveRequestFlow.DeadOutcome.Alive)]
    [InlineData(false, true, MoveRequestFlow.DeadOutcome.Alive)]
    [InlineData(true, false, MoveRequestFlow.DeadOutcome.DropOffPin)]
    [InlineData(true, true, MoveRequestFlow.DeadOutcome.RelayAtPin)]
    public void DecideDead_RelaysADeadPcOnlyAtItsPin(bool dead, bool atPin, MoveRequestFlow.DeadOutcome expected)
        => Assert.Equal(expected, MoveRequestFlow.DecideDead(dead, atPin));

    [Fact]
    public void IsAtDeadPoint_IsBitEqualityOnXAndY()
    {
        var next = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(10.5f) + 1);
        Assert.True(MoveRequestRules.IsAtDeadPoint(10.5f, 20.25f, 10.5f, 20.25f));
        Assert.False(MoveRequestRules.IsAtDeadPoint(next, 20.25f, 10.5f, 20.25f));
        Assert.False(MoveRequestRules.IsAtDeadPoint(10.5f, next, 10.5f, 20.25f));
    }

    [Fact]
    public void DecideAccepted_EndsTheImmovableChannelOnAMove()
    {
        var accepted = MoveRequestFlow.DecideAccepted(100f, 100f, 101f, 100f, 105f, 100f, 6304, id => id == 6304);
        Assert.Equal(6304u, accepted.ChannelToEnd);
        Assert.True(accepted.IsMoving);
    }

    [Fact]
    public void DecideAccepted_KeepsTheChannelWhenTheReportOnlyTurnsTheAvatar()
    {
        var called = false;
        var accepted = MoveRequestFlow.DecideAccepted(100f, 100f, 100f, 100f, 100f, 100f, 6304, _ => called = true);
        Assert.Equal(0u, accepted.ChannelToEnd);
        Assert.False(accepted.IsMoving);
        Assert.False(called); // the predicate is consulted only for a displacing report
    }

    [Fact]
    public void DecideAccepted_KeepsAMovableChannel()
    {
        var accepted = MoveRequestFlow.DecideAccepted(100f, 100f, 101f, 100f, 105f, 100f, 6304, _ => false);
        Assert.Equal(0u, accepted.ChannelToEnd);
        Assert.True(accepted.IsMoving);
    }

    [Fact]
    public void DecideAccepted_WithNoChannelNeverConsultsThePredicate()
    {
        var called = false;
        var accepted = MoveRequestFlow.DecideAccepted(0f, 0f, 50f, 0f, 60f, 0f, 0, _ => called = true);
        Assert.Equal(0u, accepted.ChannelToEnd);
        Assert.False(called);
    }

    [Theory]
    // Arrived on the spot: destination == current, below the 0.1 tolerance.
    [InlineData(100f, 100f, 100.05f, 100.05f, true)]
    // Above the tolerance on one axis: still walking.
    [InlineData(100f, 100f, 100.25f, 100f, false)]
    [InlineData(100f, 100f, 100f, 105f, false)]
    public void HasArrived_UsesThePointOneTolerance(float curX, float curY, float destX, float destY, bool arrived)
    {
        Assert.Equal(arrived, MoveRequestRules.HasArrived(curX, curY, destX, destY));
        Assert.Equal(!arrived, MoveRequestFlow.DecideAccepted(curX, curY, curX, curY, destX, destY, 0, _ => false).IsMoving);
    }
}
