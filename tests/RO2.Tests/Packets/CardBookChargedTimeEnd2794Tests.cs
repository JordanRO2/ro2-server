using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// RMI 2794 NfyCardBookChargedTimeEnd (<see cref="CardBookChargeExpiryNotifier"/>). No reference frame of
/// this opcode exists, so the body follows the client reader, which takes no parameters, and the lapse rule
/// is the one <see cref="CardBookChargeStore.Build2793"/> already sends.
/// </summary>
public class CardBookChargedTimeEnd2794Tests
{
    [Fact]
    public void Body_IsEmpty()
    {
        Assert.True(CardBookChargeExpiryNotifier.Body.IsEmpty);
    }

    [Fact]
    public void Lapse_AgreesWithTheStateThe2793Sends()
    {
        var now = new DateTime(2026, 7, 8, 7, 46, 50, DateTimeKind.Local);
        foreach (var until in new[] { now.AddTicks(-1), now, now.AddTicks(1), now.AddDays(30) })
        {
            ushort state = (ushort)(CardBookChargeStore.Build2793(until, now)[0] | CardBookChargeStore.Build2793(until, now)[1] << 8);
            bool lapsed = CardBookChargeExpiryNotifier.IsLapsed(until, now);
            Assert.Equal(state == CardBookChargeStore.StateLapsed, lapsed);
            Assert.Equal(state == CardBookChargeStore.StateCharged, !lapsed);
        }
    }

    [Fact]
    public void NextWait_RunsToTheExpiryButNeverPastTheCheckInterval()
    {
        var now = new DateTime(2026, 7, 8, 7, 0, 0, DateTimeKind.Local);
        Assert.Equal(TimeSpan.FromSeconds(90), CardBookChargeExpiryNotifier.NextWait(now.AddSeconds(90), now));
        
        Assert.Equal(CardBookChargeExpiryNotifier.CheckInterval, CardBookChargeExpiryNotifier.NextWait(now.AddDays(30), now));
        Assert.Equal(TimeSpan.Zero, CardBookChargeExpiryNotifier.NextWait(now, now));
        Assert.Equal(TimeSpan.Zero, CardBookChargeExpiryNotifier.NextWait(now.AddMinutes(-1), now));
        
        Assert.True(CardBookChargeExpiryNotifier.CheckInterval.TotalMilliseconds < int.MaxValue);
    }
}
