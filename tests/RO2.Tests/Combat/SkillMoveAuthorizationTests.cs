using RO2.Combats.Application;
using Xunit;
using R = RO2.Combats.Application.SkillMoveAuthorization.MoveRefusal;
using Npc = RO2.Combats.Application.SkillMoveAuthorization.MovedNpc;

namespace RO2.Tests.Combat;

/// <summary>Characterization of <see cref="SkillMoveAuthorization"/> (the 1523/1525 move-authorization decision moved into Combat.Application in B4 S21).</summary>
public class SkillMoveAuthorizationTests
{
    [Fact]
    public void Within_IsSquaredHorizontalDistance()
    {
        Assert.True(SkillMoveAuthorization.Within(0f, 0f, 3f, 4f, 5f));   // exactly 5 away, reach 5 -> within
        Assert.False(SkillMoveAuthorization.Within(0f, 0f, 3f, 4f, 4.99f));
    }

    [Theory]
    // hasCast, kindStoresTarget, castSelf, reach, within? -> outcome
    [InlineData(false, false, false, true, true, R.NoAcceptedCast)]      // no accepted cast first
    [InlineData(true, true, false, true, true, R.SelfNotCastTarget)]     // target-storing kind, cast not self
    [InlineData(true, false, false, false, true, R.NoReachRow)]          // no reach row (reach null)
    [InlineData(true, false, false, true, false, R.SelfOutOfReach)]      // out of reach
    [InlineData(true, true, true, true, true, R.None)]                   // allowed
    [InlineData(true, false, false, true, true, R.None)]                 // non-target kind, allowed
    public void SelfMove_Ordered(bool hasCast, bool kindStoresTarget, bool castSelf, bool hasReach, bool within, R expected)
    {
        // Place the end point at distance 0 (within) or 100 (out) with reach 10.
        float reach = hasReach ? 10f : float.NaN;
        float? reachArg = hasReach ? reach : null;
        float ex = within ? 0f : 100f;
        Assert.Equal(expected, SkillMoveAuthorization.SelfMove(hasCast, kindStoresTarget, castSelf, reachArg, 0f, 0f, ex, 0f));
    }

    [Fact]
    public void NpcMove_OrderedRefusals()
    {
        var live = new Npc(Resolved: true, IsDead: false, SameMap: true, Camp: 2, X: 0f, Y: 0f);

        Assert.Equal(R.NotTrackedNpc, SkillMoveAuthorization.NpcMove(default, true, true, 10f, 0f, 0f, 0f, 0f));
        Assert.Equal(R.NotLiveMonster, SkillMoveAuthorization.NpcMove(live with { IsDead = true }, true, true, 10f, 0f, 0f, 0f, 0f));
        Assert.Equal(R.NotLiveMonster, SkillMoveAuthorization.NpcMove(live with { SameMap = false }, true, true, 10f, 0f, 0f, 0f, 0f));
        Assert.Equal(R.NotLiveMonster, SkillMoveAuthorization.NpcMove(live with { Camp = 1 }, true, true, 10f, 0f, 0f, 0f, 0f));
        Assert.Equal(R.NotCastTarget, SkillMoveAuthorization.NpcMove(live, isCastTarget: false, true, 10f, 0f, 0f, 0f, 0f));
        Assert.Equal(R.MoveKindStoresNoTarget, SkillMoveAuthorization.NpcMove(live, true, moveKindStoresTarget: false, 10f, 0f, 0f, 0f, 0f));
        Assert.Equal(R.NoReachRow, SkillMoveAuthorization.NpcMove(live, true, true, null, 0f, 0f, 0f, 0f));
        Assert.Equal(R.MonsterOutOfReach, SkillMoveAuthorization.NpcMove(live with { X = 100f }, true, true, 10f, 0f, 0f, 0f, 0f));
        Assert.Equal(R.PointOutOfReach, SkillMoveAuthorization.NpcMove(live, true, true, 10f, 0f, 0f, 100f, 0f));
        Assert.Equal(R.None, SkillMoveAuthorization.NpcMove(live, true, true, 10f, 0f, 0f, 0f, 0f));
    }
}
