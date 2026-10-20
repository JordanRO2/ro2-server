using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Battle-ready check for a party. The leader's 1500 request opens the check and notifies the other members
/// (2699). Each member answers with 1501, and the outcome is reported to the leader (2697).
/// </summary>
public static class PartyReadyCheckService
{
 /// <summary>Time members have to answer the check, matching the 2699 dialog timeout.</summary>
    public static readonly TimeSpan AnswerWindow = TimeSpan.FromMilliseconds(10_000);

 /// <summary>A roster member as the tally sees it: its character GID, name and whether it can answer.</summary>
    internal readonly record struct RosterEntry(ulong Gid, string Name, bool CanAnswer);

    private sealed class Check
    {
        public readonly Lock Gate = new();
        public required PartyState Party;
        public required BroadcastService Broadcast;
        public required uint InitiatorSessionId;
        public required ulong InitiatorGid;
        public required string InitiatorName;
        public readonly Dictionary<ulong, byte> Votes = new();
        public bool Finished;
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

 /// <summary>Running checks, keyed by party ID.</summary>
    private static readonly ConcurrentDictionary<uint, Check> Running = new();

 /// <summary>
 /// Handles a 1500 request from <paramref name="leader"/>: opens the check and notifies the other online members,
 /// or answers 15727 when a check is already running.
 /// </summary>
    internal static async Task StartAsync(PartyService parties, BroadcastService broadcast, PartyState party,
        PlayerState leader, ProudSession session, ILogger logger)
    {
        var gid = PartyAnswerPackets.MemberGid(leader);
        var check = new Check
        {
            Party = party,
            Broadcast = broadcast,
            InitiatorSessionId = session.SessionId,
            InitiatorGid = gid,
            InitiatorName = leader.Name,
        };
        if (!Running.TryAdd(party.PartyId, check))
        {
            await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsPartyFightingTrim,
                PartyReadyCheckPackets.BuildAnsPartyFightingTrim2698(PartyReadyCheckPackets.ResultAlreadyChecking));
            logger.LogDebug("{Name}: 1500 while party {Party}'s battle-ready check is running (2698 15727)", leader.Name, party.PartyId);
            return;
        }

        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsPartyFightingTrim,
            PartyReadyCheckPackets.BuildAnsPartyFightingTrim2698(PartyReadyCheckPackets.ResultOk));
        var start = PartyReadyCheckPackets.BuildNfyPartyFightingTrimStart2699(gid);
        foreach (var memberId in MembersToNotify(party.MemberSessionIds.ToArray(),
                     id => parties.GetOnlineMember(party, id) is not null, session.SessionId))
        {
            var memberSession = broadcast.GetSession(memberId);
            if (memberSession != null)
                await memberSession.SendInternalRmiAsync(ZoneS2COpcodes.NfyPartyFightingTrimStart, start);
        }
        logger.LogInformation("{Name} started party {Party}'s battle-ready check", leader.Name, party.PartyId);

        _ = FinishWhenDueAsync(parties, check, logger);
        
        await TryFinishAsync(parties, check, timedOut: false, logger);
    }

 /// <summary>
 /// Returns the members who receive the 2699 notification: every online roster member except the initiator.
 /// </summary>
    internal static List<uint> MembersToNotify(IReadOnlyList<uint> memberSessionIds, Func<uint, bool> isOnline,
        uint initiatorSessionId)
    {
        var to = new List<uint>();
        foreach (var memberId in memberSessionIds)
            if (memberId != initiatorSessionId && isOnline(memberId))
                to.Add(memberId);
        return to;
    }

 /// <summary>
 /// Handles a 1501 vote from <paramref name="voter"/>: records it and answers. Settles the check when every
 /// answerable member has voted.
 /// </summary>
    internal static async Task VoteAsync(PartyService parties, PartyState party, PlayerState voter, ProudSession session,
        byte vote, ILogger logger)
    {
        if (!Running.TryGetValue(party.PartyId, out var check) || !ReferenceEquals(check.Party, party))
        {
            logger.LogDebug("{Name}: 1501 with no battle-ready check running; not answered", voter.Name);
            return;
        }

        var gid = PartyAnswerPackets.MemberGid(voter);
        bool second;
        lock (check.Gate)
        {
            if (check.Finished || gid == 0 || gid == check.InitiatorGid) return;
            second = !check.Votes.TryAdd(gid, vote);
        }

        if (second)
        {
            await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsPartyFightingTrimVote,
                PartyReadyCheckPackets.BuildAnsPartyFightingTrimVote2700(PartyReadyCheckPackets.ResultAlreadyVoted));
            return;
        }

        
        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsPartyFightingTrim,
            PartyReadyCheckPackets.BuildAnsPartyFightingTrim2698(PartyReadyCheckPackets.ResultOk));

        logger.LogDebug("{Name} answered party {Party}'s battle-ready check: {Vote}", voter.Name, party.PartyId,
            vote == PartyReadyCheckPackets.VoteReady ? "ready" : "not ready");
        await TryFinishAsync(parties, check, timedOut: false, logger);
    }

 /// <summary>
 /// Counts the ready and not-ready votes and collects the names of the members who are not ready. The
 /// initiator is excluded.
 /// </summary>
    internal static (byte Ready, byte NotReady, List<string> NotReadyNames) Tally(IReadOnlyList<RosterEntry> roster,
        ulong initiatorGid, IReadOnlyDictionary<ulong, byte> votes)
    {
        int ready = 0;
        var names = new List<string>();
        foreach (var member in roster)
        {
            if (member.Gid == initiatorGid) continue;
            if (votes.TryGetValue(member.Gid, out var v) && v == PartyReadyCheckPackets.VoteReady) ready++;
            else names.Add(member.Name);
        }
        return ((byte)Math.Min(ready, byte.MaxValue), (byte)Math.Min(names.Count, byte.MaxValue), names);
    }

 /// <summary>The party's roster in roster order, including offline members.</summary>
    private static List<RosterEntry> Roster(PartyService parties, PartyState party)
    {
        var roster = new List<RosterEntry>();
        foreach (var memberId in party.MemberSessionIds.ToArray())
        {
            var online = parties.GetOnlineMember(party, memberId);
            var known = online ?? party.Members.GetValueOrDefault(memberId);
            roster.Add(new RosterEntry(PartyAnswerPackets.MemberGid(known), known?.Name ?? "", online is not null));
        }
        return roster;
    }

    private static async Task FinishWhenDueAsync(PartyService parties, Check check, ILogger logger)
    {
        try
        {
            
            await check.Done.Task.WaitAsync(AnswerWindow).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (!check.Done.Task.IsCompleted)
                await TryFinishAsync(parties, check, timedOut: true, logger);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Battle-ready check of party {Party} failed to settle", check.Party.PartyId);
        }
    }

 /// <summary>
 /// Settles a check once every answerable member has voted or the answer window has expired, and reports the
 /// result to the initiator while it is still online.
 /// </summary>
    private static async Task TryFinishAsync(PartyService parties, Check check, bool timedOut, ILogger logger)
    {
        (byte Ready, byte NotReady, List<string> NotReadyNames) tally;
        lock (check.Gate)
        {
            if (check.Finished) return;
            var roster = Roster(parties, check.Party);
            if (!timedOut && roster.Any(m => m.Gid != check.InitiatorGid && m.CanAnswer && !check.Votes.ContainsKey(m.Gid)))
                return;
            check.Finished = true;
            tally = Tally(roster, check.InitiatorGid, check.Votes);
        }
        Running.TryRemove(new KeyValuePair<uint, Check>(check.Party.PartyId, check));
        check.Done.TrySetResult();

        if (!StillExists(parties, check.Party))
        {
            logger.LogDebug("Party {Party} is gone; its battle-ready check ends without a result", check.Party.PartyId);
            return;
        }
        
        var initiator = parties.GetOnlineMember(check.Party, check.InitiatorSessionId);
        var initiatorSession = check.Broadcast.GetSession(check.InitiatorSessionId);
        if (initiator is null || initiatorSession is null
            || PartyAnswerPackets.MemberGid(initiator) != check.InitiatorGid
            || !ReferenceEquals(parties.GetPlayerParty(check.InitiatorSessionId), check.Party))
        {
            logger.LogDebug("Party {Party}'s battle-ready check ended after {Name} left it; no result sent",
                check.Party.PartyId, check.InitiatorName);
            return;
        }
        await initiatorSession.SendInternalRmiAsync(ZoneS2COpcodes.NfyPartyFightingTrimResult,
            PartyReadyCheckPackets.BuildNfyPartyFightingTrimResult2697(tally.Ready, tally.NotReady, tally.NotReadyNames));
        logger.LogInformation("Party {Party}'s battle-ready check asked by {Name}: {Ready} ready, {NotReady} not ({Names}){Late}",
            check.Party.PartyId, check.InitiatorName, tally.Ready, tally.NotReady, string.Join(", ", tally.NotReadyNames),
            timedOut ? " after the answer window" : "");
    }

 /// <summary>Whether the party still exists: at least one member still maps to it.</summary>
    private static bool StillExists(PartyService parties, PartyState party)
    {
        foreach (var memberId in party.MemberSessionIds.ToArray())
            if (ReferenceEquals(parties.GetPlayerParty(memberId), party))
                return true;
        return false;
    }
}
