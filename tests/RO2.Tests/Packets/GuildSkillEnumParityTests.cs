using System;
using RO2.Guilds.Domain;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// The host guild-skill result codes are produced by casting the host enums to int and matching them to the Domain
/// enums (GuildSkillRules). That parity is relied on with no guard, so a future reorder of either side would silently
/// drift the 2722 learn-refusal and the skill-book verdict codes. These asserts pin the ordinals together.
/// </summary>
public class GuildSkillEnumParityTests
{
    [Fact]
    public void LearnRefusal_ordinals_match_the_domain_enum()
    {
        Assert.Equal((int)GuildSkillLearnRefusal.None, (int)GuildSkillLearnPackets.LearnRefusal.None);
        Assert.Equal((int)GuildSkillLearnRefusal.NotEnoughPoints, (int)GuildSkillLearnPackets.LearnRefusal.NotEnoughPoints);
        Assert.Equal((int)GuildSkillLearnRefusal.WrongSkillIndex, (int)GuildSkillLearnPackets.LearnRefusal.WrongSkillIndex);
        Assert.Equal((int)GuildSkillLearnRefusal.MissingNeedSkill1, (int)GuildSkillLearnPackets.LearnRefusal.MissingNeedSkill1);
        Assert.Equal((int)GuildSkillLearnRefusal.MissingNeedSkill2, (int)GuildSkillLearnPackets.LearnRefusal.MissingNeedSkill2);
        Assert.Equal(Enum.GetValues<GuildSkillLearnRefusal>().Length, Enum.GetValues<GuildSkillLearnPackets.LearnRefusal>().Length);
    }

    [Fact]
    public void SkillBookVerdict_ordinals_match_the_domain_enum()
    {
        Assert.Equal((int)GuildSkillBookVerdict.Learn, (int)GuildSkillBookLearn.Verdict.Learn);
        Assert.Equal((int)GuildSkillBookVerdict.AlreadyLearned, (int)GuildSkillBookLearn.Verdict.AlreadyLearned);
        Assert.Equal((int)GuildSkillBookVerdict.PreviousLevelMissing, (int)GuildSkillBookLearn.Verdict.PreviousLevelMissing);
        Assert.Equal((int)GuildSkillBookVerdict.NotLearnable, (int)GuildSkillBookLearn.Verdict.NotLearnable);
        Assert.Equal(Enum.GetValues<GuildSkillBookVerdict>().Length, Enum.GetValues<GuildSkillBookLearn.Verdict>().Length);
    }
}
