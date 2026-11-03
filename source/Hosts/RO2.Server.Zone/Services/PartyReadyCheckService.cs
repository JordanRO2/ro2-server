using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RO2.Network.ProudNet;
using RO2.Parties.Application;
using RO2.ReadyChecks.Application;
using RO2.ReadyChecks.Domain;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Battle-ready check for a party. The leader's 1500 request opens the check and notifies the other members
/// (2699). Each member answers with 1501, and the outcome is reported to the leader (2697). The decisions are the
/// <see cref="ReadyCheck"/> aggregate's; this host service keeps the running checks, the per-check gate, the answer
/// window timer and the sends.
/// </summary>
public sealed class PartyReadyCheckService(IPartyRoster parties, BroadcastService broadcast, ILogger<PartyReadyCheckService> logger)
{
 /// <summary>Time members have to answer the check, matching the 2699 dialog timeout.</summary>
    public static readonly TimeSpan AnswerWindow = TimeSpan.FromMilliseconds(10_000);

    private readonly TimeSpan _window = AnswerWindow;

 /// <summary>Test seam: a shorter answer window so the timeout settle can be driven.</summary>
    internal PartyReadyCheckService(IPartyRoster parties, BroadcastService broadcast, ILogger<PartyReadyCheckService> logger, TimeSpan window)
        : this(parties, broadcast, logger) => _window = window;

    private sealed class Check(ReadyCheck state, string initiatorName)
    {
        public readonly Lock Gate = new();
        public readonly ReadyCheck State = state;
        public readonly string InitiatorName = initiatorName;
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

 /// <summary>Running checks, keyed by party ID.</summary>
    private readonly ConcurrentDictionary<uint, Check> _running = new();

 /// <summary>
 /// Handles a 1500 request from <paramref name="leader"/>: opens the check and notifies the other online members,
 /// or answers 15727 when a check is already running.
 /// </summary>
    internal async Task StartAsync(uint partyId, PlayerState leader, ProudSession session)
    {
        var gid = PartyAnswerPackets.MemberGid(leader);
        var check = new Check(new ReadyCheck(partyId, session.SessionId, gid), leader.Name);
        var alreadyChecking = !_running.TryAdd(partyId, check);
        var roster = parties.RosterOfParty(partyId);
        var effects = check.State.Start(alreadyChecking, ReadyCheckRoster.SessionIds(roster),
            id => ReadyCheckRoster.IsOnline(roster, id));
        await SendAsync(effects, check, session);
        if (alreadyChecking)
        {
            logger.LogDebug("{Name}: 1500 while party {Party}'s battle-ready check is running (2698 15727)", leader.Name, partyId);
            return;
        }
        logger.LogInformation("{Name} started party {Party}'s battle-ready check", leader.Name, partyId);

        _ = FinishWhenDueAsync(check);

        await TryFinishAsync(check, timedOut: false);
    }

 /// <summary>
 /// Handles a 1501 vote from <paramref name="voter"/>: records it and answers. Settles the check when every
 /// answerable member has voted.
 /// </summary>
    internal async Task VoteAsync(uint partyId, PlayerState voter, ProudSession session, byte vote)
    {
        if (!_running.TryGetValue(partyId, out var check))
        {
            logger.LogDebug("{Name}: 1501 with no battle-ready check running; not answered", voter.Name);
            return;
        }

        IReadOnlyList<ReadyCheckEffect> effects;
        lock (check.Gate) effects = check.State.Vote(PartyAnswerPackets.MemberGid(voter), vote);
        if (effects.Count == 0) return;

        await SendAsync(effects, check, session);
        if (effects[0] is not AckOk) return;

        logger.LogDebug("{Name} answered party {Party}'s battle-ready check: {Vote}", voter.Name, partyId,
            vote == ReadyCheck.VoteReady ? "ready" : "not ready");
        await TryFinishAsync(check, timedOut: false);
    }

    private async Task FinishWhenDueAsync(Check check)
    {
        try
        {
            await check.Done.Task.WaitAsync(_window).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (!check.Done.Task.IsCompleted)
                await TryFinishAsync(check, timedOut: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Battle-ready check of party {Party} failed to settle", check.State.PartyId);
        }
    }

 /// <summary>
 /// Settles a check once every answerable member has voted or the answer window has expired, and reports the
 /// result to the initiator while it is still in the party.
 /// </summary>
    private async Task TryFinishAsync(Check check, bool timedOut)
    {
        var state = check.State;
        var initiatorSession = broadcast.GetSession(state.InitiatorSessionId);
        IReadOnlyList<ReadyCheckEffect> effects;
        lock (check.Gate)
        {
            var present = initiatorSession is not null
                && ReadyCheckRoster.InitiatorPresent(parties, state.PartyId, state.InitiatorSessionId, state.InitiatorGid);
            effects = state.Settle(ReadyCheckRoster.Entries(parties.RosterOfParty(state.PartyId)), timedOut, present);
        }
        if (effects.Count == 0) return;

        _running.TryRemove(new KeyValuePair<uint, Check>(state.PartyId, check));
        check.Done.TrySetResult();

        if (initiatorSession is null || effects[0] is not CheckResult result)
        {
            logger.LogDebug("Party {Party}'s battle-ready check ended after {Name} left it; no result sent",
                state.PartyId, check.InitiatorName);
            return;
        }
        await SendAsync(effects, check, initiatorSession);
        logger.LogInformation("Party {Party}'s battle-ready check asked by {Name}: {Ready} ready, {NotReady} not ({Names}){Late}",
            state.PartyId, check.InitiatorName, result.Ready, result.NotReady, string.Join(", ", result.NotReadyNames),
            timedOut ? " after the answer window" : "");
    }

 /// <summary>Sends each effect's frame, in order: a 2699 to its member, the rest to <paramref name="requester"/>.</summary>
    private async Task SendAsync(IReadOnlyList<ReadyCheckEffect> effects, Check check, ProudSession requester)
    {
        foreach (var effect in effects)
        {
            if (PartyReadyCheckFrames.ToFrame(effect, check.State.InitiatorGid) is not { } frame) continue;
            var to = effect is NotifyStart n ? broadcast.GetSession(n.ToSessionId) : requester;
            if (to != null) await to.SendInternalRmiAsync(frame.Opcode, frame.Body);
        }
    }
}
