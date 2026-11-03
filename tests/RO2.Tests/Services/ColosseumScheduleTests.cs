using RO2.Colosseum.Domain;
using RO2.GameData;
using Xunit;

namespace RO2.Tests.Services;

/// <summary>Stateless tests of the Colosseum opening hours and the 2879 body.</summary>
public class ColosseumScheduleTests
{
    private static readonly int[] Hours = { 1, 13, 18 };

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
        => Assert.Equal(expected, ColosseumSchedule.IsOpen(At(hour), Hours));

    [Fact]
    public void IsOpen_HonorsTheConfiguredHours()
    {
        Assert.True(ColosseumSchedule.IsOpen(At(5), new[] { 5 }));
        Assert.False(ColosseumSchedule.IsOpen(At(1), new[] { 5 }));
    }

    [Fact]
    public void AnnouncesOnEntry_FollowsLevelMapAndHours()
    {
        Assert.False(ColosseumSchedule.AnnouncesOnEntry(At(13), 1, null, Hours, (byte)2));
        Assert.True(ColosseumSchedule.AnnouncesOnEntry(At(13), 2, null, Hours, (byte)2));
        Assert.True(ColosseumSchedule.AnnouncesOnEntry(At(13), 50, new MapData { Map_Type = 1 }, Hours, (byte)2));
        Assert.False(ColosseumSchedule.AnnouncesOnEntry(At(13), 50, new MapData { Map_Type = 3 }, Hours, (byte)2));
        Assert.False(ColosseumSchedule.AnnouncesOnEntry(At(12), 50, null, Hours, (byte)2));
    }

    [Fact]
    public void Build2879_IsOneByte()
    {
        Assert.Equal(new byte[] { 0x01 }, ColosseumSchedule.Build2879(true));
        Assert.Equal(new byte[] { 0x00 }, ColosseumSchedule.Build2879(false));
    }
}
