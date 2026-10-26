using System;
using RO2.Combats.Domain;
using Xunit;

namespace RO2.Tests.Combat;

/// <summary>Characterization of <see cref="ItemSkillRepeatWindow"/> (the 1000 ms item-skill guard moved out of the host in B4 S12).</summary>
public class ItemSkillRepeatWindowTests
{
    [Fact]
    public void Window_Is1000ms() => Assert.Equal(TimeSpan.FromMilliseconds(1000), ItemSkillRepeatWindow.RepeatWindow);

    [Fact]
    public void NoAnswerYet_IsNotRepeat()
    {
        var w = new ItemSkillRepeatWindow();
        Assert.False(w.IsRepeat(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void WithinWindow_IsRepeat_AtAndPastIt_IsNot()
    {
        var w = new ItemSkillRepeatWindow();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        w.MarkAnswered(t0);

        Assert.True(w.IsRepeat(t0));                          // the same instant is a repeat
        Assert.True(w.IsRepeat(t0.AddMilliseconds(999)));     // inside the window
        Assert.False(w.IsRepeat(t0.AddMilliseconds(1000)));   // exactly the window is clear (strict <)
        Assert.False(w.IsRepeat(t0.AddMilliseconds(1001)));   // past it
    }

    [Fact]
    public void AnswerBeforeNow_GuardsOnlyForward()
    {
        var w = new ItemSkillRepeatWindow();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        w.MarkAnswered(t0);

        Assert.False(w.IsRepeat(t0.AddMilliseconds(-1)));   // a now before the answer is never a repeat
    }

    [Fact]
    public void MarkAnswered_MovesTheWindow()
    {
        var w = new ItemSkillRepeatWindow();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        w.MarkAnswered(t0);
        w.MarkAnswered(t0.AddMilliseconds(500));             // a second answer restarts the window from here

        Assert.True(w.IsRepeat(t0.AddMilliseconds(1400)));   // 900 ms after the latest answer
        Assert.False(w.IsRepeat(t0.AddMilliseconds(1500)));  // 1000 ms after the latest answer
    }
}
