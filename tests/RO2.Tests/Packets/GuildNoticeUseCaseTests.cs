using RO2.Guilds.Application;
using RO2.Guilds.Domain;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>The Guild.Application notice change: validate (0/7/15, max 100) + one ledger write, no host involved.</summary>
public class GuildNoticeUseCaseTests
{
    private sealed class CountingLedger : IGuildNoticeLedger
    {
        public readonly List<string> Writes = [];
        public Task SetNoticeAsync(Guild guild, string notice) { Writes.Add(notice); return Task.CompletedTask; }
    }

    [Fact]
    public async Task Set_AcceptsUpToTheLimitAndWritesOnce()
    {
        var ledger = new CountingLedger();

        var r = await new GuildNoticeUseCase(ledger).SetAsync(new Guild(), GuildNoticeUseCase.MaxNoticeCount, "hello");

        Assert.Equal((ushort)0, r);
        Assert.Equal(new[] { "hello" }, ledger.Writes);
    }

    [Fact]
    public async Task Set_TooLongIsRefusedWithCode15AndWritesNothing()
    {
        var ledger = new CountingLedger();

        var r = await new GuildNoticeUseCase(ledger).SetAsync(new Guild(), 101, "x");

        Assert.Equal((ushort)15, r);
        Assert.Empty(ledger.Writes);
    }

    [Fact]
    public async Task Set_WithoutAGuildIsRefusedWithCode7AndWritesNothing()
    {
        var ledger = new CountingLedger();

        var r = await new GuildNoticeUseCase(ledger).SetAsync(null, 5, "x");

        Assert.Equal((ushort)7, r);
        Assert.Empty(ledger.Writes);
        Assert.Equal(100, GuildNoticeUseCase.MaxNoticeCount);
    }
}
