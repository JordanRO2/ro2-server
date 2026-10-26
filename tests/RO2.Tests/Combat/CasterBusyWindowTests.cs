using System;
using RO2.Combats.Domain;
using Xunit;

namespace RO2.Tests.Combat;

/// <summary>Characterization of <see cref="CasterBusyWindow"/> (the cooldown window moved out of the host in B4 S12).</summary>
public class CasterBusyWindowTests
{
    [Fact]
    public void NoCast_IsIdle()
    {
        var w = new CasterBusyWindow();
        Assert.True(w.IsIdle(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void WithinDuration_IsBusy_PastIt_IsIdle()
    {
        var w = new CasterBusyWindow();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        w.Record(t0, TimeSpan.FromSeconds(1));

        Assert.False(w.IsIdle(t0));                          // the start is not past the duration
        Assert.False(w.IsIdle(t0.AddMilliseconds(1000)));    // exactly the duration is still busy (strict >)
        Assert.True(w.IsIdle(t0.AddMilliseconds(1001)));     // one tick past the duration is idle
    }

    [Fact]
    public void ZeroDuration_IsIdleOnceTheStartHasPassed()
    {
        var w = new CasterBusyWindow();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        w.Record(t0, TimeSpan.Zero);

        Assert.False(w.IsIdle(t0));                      // at the start, not yet past zero
        Assert.True(w.IsIdle(t0.AddMilliseconds(1)));    // any time after the start is idle
    }

    [Fact]
    public void Record_ReplacesThePriorCast()
    {
        var w = new CasterBusyWindow();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        w.Record(t0, TimeSpan.FromSeconds(10));
        Assert.False(w.IsIdle(t0.AddSeconds(5)));

        w.Record(t0.AddSeconds(5), TimeSpan.FromSeconds(1));  // a shorter cast resets the window to its start
        Assert.True(w.IsIdle(t0.AddSeconds(7)));
    }
}
