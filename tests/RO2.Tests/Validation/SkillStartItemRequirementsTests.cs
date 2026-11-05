using RO2.GameData;
using RO2.Server.Zone.Handlers.Rmi;
using Xunit;

namespace RO2.Tests.Validation;

/// <summary>CB-H10: the 1561 item cast's Require_Level / Require_Job gate (the handler is not test-instantiable).</summary>
public class SkillStartItemRequirementsTests
{
    [Fact]
    public void UnderleveledPlayer_DoesNotMeetRequirements()
    {
        var item = new ItemData { Require_Level = 30 };
        Assert.False(SkillStartItemHandler.MeetsRequirements(item, level: 29, job: 1));
        Assert.True(SkillStartItemHandler.MeetsRequirements(item, level: 30, job: 1));
    }

    [Fact]
    public void RequireJobMask_UsesBitJobMinusOne()
    {
        var item = new ItemData { Require_Job = 1L << 1 }; // job 2 only
        Assert.False(SkillStartItemHandler.MeetsRequirements(item, level: 99, job: 1));
        Assert.True(SkillStartItemHandler.MeetsRequirements(item, level: 99, job: 2));
        Assert.True(SkillStartItemHandler.MeetsRequirements(new ItemData(), level: 1, job: 0));
    }
}
