using RO2.Parties.Application;
using RO2.Parties.Domain;
using Xunit;

namespace RO2.Tests.Parties;

/// <summary>The effect dispatch of RO2.Party.Application and the aggregate rebuild the host snapshots a party with.</summary>
public class PartyApplicationTests
{
    private sealed class Recorder : IPartyNotifier
    {
        public List<string> Calls { get; } = [];
        public Task SendToMemberAsync(ulong gid, PartyMessage message) { Calls.Add($"member {gid} {message.Kind}"); return Task.CompletedTask; }
        public Task SendToPartyAsync(PartyMessage message, ulong? exceptGid) { Calls.Add($"party {message.Kind} except {exceptGid}"); return Task.CompletedTask; }
        public Task SendRosterAsync(bool create, ulong? onlyGid) { Calls.Add($"roster {create} {onlyGid}"); return Task.CompletedTask; }
        public Task SendJoinDeltaAsync(ulong joinerGid) { Calls.Add($"delta {joinerGid}"); return Task.CompletedTask; }
        public Task SendDestroyAsync(int copies, IReadOnlyList<ulong> recipients) { Calls.Add($"destroy {copies} {string.Join(',', recipients)}"); return Task.CompletedTask; }
        public Task SendLayoffAsync(ulong gid) { Calls.Add($"layoff {gid}"); return Task.CompletedTask; }
        public void ScheduleLockSweep() => Calls.Add("sweep");
    }

    [Fact]
    public async Task Applier_DeliversEveryEffectKind_InTheOrderTheDecisionListedThem()
    {
        var recorder = new Recorder();
        await PartyEffectApplier.ApplyAsync(
        [
            new ScheduleLockSweep(),
            new SendToMember(1, new PartyMessage(PartyMessageKind.Error)),
            new SendToParty(new PartyMessage(PartyMessageKind.Out)),
            new SendToPartyExcept(2, new PartyMessage(PartyMessageKind.NfyTypeChange)),
            new SendJoinDelta(3),
            new SendRosterCreate(null),
            new SendRosterCreate(3),
            new SendRosterUpdate(),
            new SendDestroy(2, [4, 5]),
            new SendLayoff(6),
        ], recorder);

        Assert.Equal(["sweep", "member 1 Error", "party Out except ", "party NfyTypeChange except 2", "delta 3",
            "roster True ", "roster True 3", "roster False ", "destroy 2 4,5", "layoff 6"], recorder.Calls);
    }

    [Fact]
    public void Restore_RebuildsTheStateTheHostHolds_SoADecisionActsOnIt()
    {
        var (leader, second, third) = (0x100UL, 0x101UL, 0x102UL);
        var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        var party = PartyAggregate.Restore(7, leader, now, second, 2, [leader, second, third],
            new Dictionary<ulong, byte> { [leader] = 1, [second] = 2, [third] = 4 }, new Dictionary<ulong, byte> { [third] = 1 },
            [], new Dictionary<ulong, DateTime> { [third] = now }, [leader, 0, 0]);

        Assert.Equal([leader, second, third], party.Members);
        Assert.Equal(second, party.LeaderGid);
        Assert.Equal(PartyAggregate.RaidMaxMembers, party.MaxMembers);
        Assert.Equal(4, party.FrameSlotOf(third));
        Assert.Equal(1, party.HelperFlags[third]);
        Assert.Equal([third], party.ExpiredLocks(now + PartyAggregate.LockedMemberLifetime));
        Assert.Equal([leader, 0UL, 0UL], party.SubMasters);

        var (outcome, _) = party.DecideLeave(second, PartyLeaveReason.Left, new HashSet<ulong> { leader, third });
        Assert.Equal(PartyResult.Done, outcome.Result);
        Assert.Equal(leader, party.LeaderGid);
    }
}
