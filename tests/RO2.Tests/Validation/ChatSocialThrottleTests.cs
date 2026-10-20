using RO2.Server.Chat.Services;
using Xunit;

namespace RO2.Tests.Validation;

/// <summary>
/// Tests for <see cref="ChatSocialThrottle"/>, which bounds the per-character rate of messenger social requests (add
/// and remove friend, add and remove block, list fetches), so one connection cannot drive unbounded database work.
/// The clock is injected for determinism.
/// </summary>
public sealed class ChatSocialThrottleTests
{
    private const uint Alice = 1001;
    private const uint Bob = 2002;

    [Fact]
    public void AdmitsLimitThenRefusesWithinWindow()
    {
        var throttle = new ChatSocialThrottle();
        for (var i = 0; i < ChatSocialThrottle.Limit; i++)
            Assert.True(throttle.TryAct(Alice, 1000 + i), $"request {i}");

        Assert.False(throttle.TryAct(Alice, 1000 + ChatSocialThrottle.Limit));
    }

    [Fact]
    public void BudgetIsSharedAcrossAllSocialRequests_NotPerOpcode()
    {
        
        
        var throttle = new ChatSocialThrottle();
        for (var i = 0; i < ChatSocialThrottle.Limit; i++)
            Assert.True(throttle.TryAct(Alice, 500));

        
        Assert.False(throttle.TryAct(Alice, 500));
    }

    [Fact]
    public void CharactersAreIndependent()
    {
        var throttle = new ChatSocialThrottle();
        for (var i = 0; i < ChatSocialThrottle.Limit; i++)
            Assert.True(throttle.TryAct(Alice, 2000));
        Assert.False(throttle.TryAct(Alice, 2000));

        
        for (var i = 0; i < ChatSocialThrottle.Limit; i++)
            Assert.True(throttle.TryAct(Bob, 2000), $"bob {i}");
        Assert.False(throttle.TryAct(Bob, 2000));
    }

    [Fact]
    public void RefillsAfterTheWindow()
    {
        var throttle = new ChatSocialThrottle();
        for (var i = 0; i < ChatSocialThrottle.Limit; i++)
            Assert.True(throttle.TryAct(Alice, 100));
        Assert.False(throttle.TryAct(Alice, 100 + ChatSocialThrottle.WindowMs - 1));

        
        for (var i = 0; i < ChatSocialThrottle.Limit; i++)
            Assert.True(throttle.TryAct(Alice, 100 + ChatSocialThrottle.WindowMs), $"refill {i}");
    }
}
