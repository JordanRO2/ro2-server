using RO2.Worlds.Application;
using RO2.Worlds.Domain;
using Xunit;

namespace RO2.Tests.World;

/// <summary>
/// The pure spawn-visibility decisions: who observes a player entering or leaving the view, which messages show it, which
/// NPCs a map load announces and when a view-data request is answered. The wire bodies stay pinned by the packet tests.
/// </summary>
public class SpawnVisibilityTests
{
    private sealed record Viewer(uint MapId, uint SessionId);

    [Fact]
    public void Audience_IsTheSubjectsMapAndSession()
    {
        var audience = SpawnVisibilityUseCase.AudienceOf(1100, 7);
        Assert.Equal(1100u, audience.MapId);
        Assert.Equal(7u, audience.ExcludedSessionId);
    }

    [Fact]
    public void Observers_AreTheOthersOnTheMap_InTheirOriginalOrder()
    {
        var onMap = new[] { new Viewer(1100, 9), new Viewer(1100, 7), new Viewer(1100, 3) };
        var observers = SpawnVisibilityUseCase.Observers(SpawnVisibilityUseCase.AudienceOf(1100, 7), onMap, v => v.MapId, v => v.SessionId);
        Assert.Equal(new uint[] { 9, 3 }, observers.Select(v => v.SessionId));
    }

    [Fact]
    public void Observers_ExcludeAnotherMap()
    {
        var all = new[] { new Viewer(1100, 9), new Viewer(1200, 3) };
        var observers = SpawnVisibilityUseCase.Observers(SpawnVisibilityUseCase.AudienceOf(1100, 7), all, v => v.MapId, v => v.SessionId);
        Assert.Equal(new uint[] { 9 }, observers.Select(v => v.SessionId));
    }

    [Fact]
    public void Observers_AloneOnTheMap_IsEmpty()
    {
        var observers = SpawnVisibilityUseCase.Observers(SpawnVisibilityUseCase.AudienceOf(1100, 7),
            new[] { new Viewer(1100, 7) }, v => v.MapId, v => v.SessionId);
        Assert.Empty(observers);
    }

    [Fact]
    public void Audience_Includes_ExcludesOnlyTheSubjectsSessionOnTheSameMap()
    {
        var audience = SpawnVisibilityRules.AudienceOf(1100, 7);
        Assert.True(audience.Includes(1100, 8));
        Assert.False(audience.Includes(1100, 7));
        Assert.False(audience.Includes(1101, 8));
        Assert.False(audience.Includes(1101, 7));
    }

    [Fact]
    public void ExchangeSteps_WithoutGuild_IsRecordViewRecordSpeed()
        => Assert.Equal(new[] { SpawnViewStep.PcIn, SpawnViewStep.PcViewData, SpawnViewStep.MoveSpeed },
            SpawnVisibilityUseCase.ExchangeSteps(false));

    [Fact]
    public void ExchangeSteps_WithGuild_AddsTheGuildViewInfoLast()
        => Assert.Equal(new[] { SpawnViewStep.PcIn, SpawnViewStep.PcViewData, SpawnViewStep.MoveSpeed, SpawnViewStep.GuildViewInfo },
            SpawnVisibilityUseCase.ExchangeSteps(true));

    [Theory]
    [InlineData(false, true, NpcAnnouncement.Announce)]
    [InlineData(false, false, NpcAnnouncement.NoTemplate)]
    [InlineData(true, true, NpcAnnouncement.Skip)]
    [InlineData(true, false, NpcAnnouncement.Skip)]   // dead wins: never counted as missing a template
    public void Announcement_DeadIsSkipped_ThenTemplateGates(bool isDead, bool hasTemplate, NpcAnnouncement expected)
        => Assert.Equal(expected, SpawnVisibilityUseCase.ClassifyAnnouncement(isDead, hasTemplate));

    [Theory]
    [InlineData(false, ViewDataReply.UnknownActor)]
    [InlineData(true, ViewDataReply.Reply)]
    public void ViewData_UnknownIsNotAnswered_KnownWithTemplateIs(bool known, ViewDataReply expected)
        => Assert.Equal(expected, SpawnVisibilityUseCase.DecideViewData(known, () => true));

    [Fact]
    public void ViewData_KnownWithoutTemplate_IsNotAnswered()
        => Assert.Equal(ViewDataReply.NoClientTemplate, SpawnVisibilityUseCase.DecideViewData(true, () => false));

    [Fact]
    public void ViewData_UnknownActor_NeverReadsTheCatalog()
    {
        var read = false;
        var reply = SpawnVisibilityUseCase.DecideViewData(false, () => { read = true; return true; });
        Assert.Equal(ViewDataReply.UnknownActor, reply);
        Assert.False(read);
    }
}
