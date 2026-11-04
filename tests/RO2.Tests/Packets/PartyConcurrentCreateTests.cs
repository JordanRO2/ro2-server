using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Audit [19]: several targets invited by a partyless inviter accept at the same moment. Party creation serializes on
/// one shared gate, so the loser joins the party the winner created instead of founding a second one.
/// </summary>
public class PartyConcurrentCreateTests
{
    [Fact]
    public async Task SimultaneousAcceptsFromAPartylessInviter_FormOneParty()
    {
        for (int round = 0; round < 15; round++)
        {
            await using var rig = await PartyWireRig.StartAsync(5);
            var inviter = rig.AddPlayer(0, "Inviter");
            var targets = Enumerable.Range(1, 4).Select(i => rig.AddPlayer(i, "T" + i)).ToArray();
            foreach (var t in targets)
                await rig.Party.InviteToPartyAsync(inviter, t, t.Name);

            using var start = new ManualResetEventSlim();
            var accepts = targets.Select((t, i) => Task.Run(async () =>
            {
                start.Wait();
                await rig.Party.AnswerInviteAsync(t, rig.Handle(i + 1), 0, inviter.CharacterUid, "Inviter");
            })).ToArray();
            start.Set();
            await Task.WhenAll(accepts);

            var party = rig.Party.GetPlayerParty(inviter.SessionId);
            Assert.NotNull(party);
            Assert.Equal(5, party.MemberSessionIds.Count);
            foreach (var t in targets)
                Assert.Same(party, rig.Party.GetPlayerParty(t.SessionId));
        }
    }
}
