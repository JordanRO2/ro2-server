using RO2.Server.Login.Services;
using Xunit;

namespace RO2.Tests.Identity;

/// <summary>
/// Tests the second-password throttle, which bounds PIN guessing per account. The bound is keyed by account id, so it persists across reconnects. The clock is passed in.
/// </summary>
public sealed class SecondPasswordThrottleTests
{
    private const uint Account = 7001;
    private const uint Other = 8002;

    [Fact]
    public void AdmitsTheBudgetThenRefusesWithinWindow()
    {
        var throttle = new SecondPasswordThrottle();
        for (var i = 0; i < SecondPasswordThrottle.MaxAttemptsPerAccount; i++)
            Assert.True(throttle.TryAttempt(Account, 1000 + i), $"attempt {i}");

        Assert.False(throttle.TryAttempt(Account, 1000 + SecondPasswordThrottle.MaxAttemptsPerAccount));
    }

    [Fact]
    public void ReconnectDoesNotResetTheBudget_BecauseItIsKeyedByAccount()
    {
        
        
        var throttle = new SecondPasswordThrottle();
        for (var i = 0; i < SecondPasswordThrottle.MaxAttemptsPerAccount; i++)
            Assert.True(throttle.TryAttempt(Account, 500));

        Assert.False(throttle.TryAttempt(Account, 500)); 
    }

    [Fact]
    public void CorrectPinResetsTheBudget()
    {
        var throttle = new SecondPasswordThrottle();
        for (var i = 0; i < SecondPasswordThrottle.MaxAttemptsPerAccount - 1; i++)
            Assert.True(throttle.TryAttempt(Account, 2000));

        
        
        throttle.Reset(Account);
        for (var i = 0; i < SecondPasswordThrottle.MaxAttemptsPerAccount; i++)
            Assert.True(throttle.TryAttempt(Account, 2000), $"post-reset attempt {i}");
        Assert.False(throttle.TryAttempt(Account, 2000));
    }

    [Fact]
    public void AccountsAreIndependent()
    {
        var throttle = new SecondPasswordThrottle();
        for (var i = 0; i < SecondPasswordThrottle.MaxAttemptsPerAccount; i++)
            Assert.True(throttle.TryAttempt(Account, 3000));
        Assert.False(throttle.TryAttempt(Account, 3000));

        for (var i = 0; i < SecondPasswordThrottle.MaxAttemptsPerAccount; i++)
            Assert.True(throttle.TryAttempt(Other, 3000), $"other {i}");
    }

    [Fact]
    public void RefillsAfterTheWindow()
    {
        var throttle = new SecondPasswordThrottle();
        for (var i = 0; i < SecondPasswordThrottle.MaxAttemptsPerAccount; i++)
            Assert.True(throttle.TryAttempt(Account, 100));
        Assert.False(throttle.TryAttempt(Account, 100 + SecondPasswordThrottle.WindowMs - 1));

        for (var i = 0; i < SecondPasswordThrottle.MaxAttemptsPerAccount; i++)
            Assert.True(throttle.TryAttempt(Account, 100 + SecondPasswordThrottle.WindowMs), $"refill {i}");
    }
}
