using RO2.Colosseum.Domain;
using RO2.GameData;
using Xunit;

namespace RO2.Tests.Services;

/// <summary>Stateless tests of the Colosseum opening hours and the 2879 body.</summary>
public class ColosseumScheduleTests
{
    private static DateTimeOffset At(int hour) => new(2026, 1, 1, hour, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1, true)]
    [InlineData(13, true)]
    [InlineData(18, true)]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(12, false)]
    [InlineData(14, false)]
    [InlineData(19, false)]
    [InlineData(23, false)]
    public void IsOpen_OnlyAtTheThreeWindows(int hour, bool expected)
        => Assert.Equal(expected, ColosseumSchedule.IsOpen(At(hour)));

    [Fact]
    public void AnnouncesOnEntry_FollowsLevelMapAndHours()
    {
        Assert.False(ColosseumSchedule.AnnouncesOnEntry(At(13), 1, null));
        Assert.True(ColosseumSchedule.AnnouncesOnEntry(At(13), 2, null));
        Assert.True(ColosseumSchedule.AnnouncesOnEntry(At(13), 50, new MapData { Map_Type = 1 }));
        Assert.False(ColosseumSchedule.AnnouncesOnEntry(At(13), 50, new MapData { Map_Type = 3 }));
        Assert.False(ColosseumSchedule.AnnouncesOnEntry(At(12), 50, null));
    }

    [Fact]
    public void Build2879_IsOneByte()
    {
        Assert.Equal(new byte[] { 0x01 }, ColosseumSchedule.Build2879(true));
        Assert.Equal(new byte[] { 0x00 }, ColosseumSchedule.Build2879(false));
    }
}
