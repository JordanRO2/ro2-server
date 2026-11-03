namespace RO2.ReadyChecks.Domain;

/// <summary>A roster member as the tally sees it: its character GID, name and whether it can answer.</summary>
public readonly record struct RosterEntry(ulong Gid, string Name, bool CanAnswer);

/// <summary>
/// One battle-ready check of a party: the votes so far and whether it has settled. Pure decisions: it takes the
/// roster snapshot the host read and returns the effects to send, in order. Not thread-safe, the host serializes
/// calls on one check.
/// </summary>
public sealed class ReadyCheck
{
    /// <summary>Vote value for the dialog's Ready button.</summary>
    public const byte VoteReady = 1;

    private readonly Dictionary<ulong, byte> _votes = new();

    public ReadyCheck(uint partyId, uint initiatorSessionId, ulong initiatorGid)
    {
        PartyId = partyId;
        InitiatorSessionId = initiatorSessionId;
        InitiatorGid = initiatorGid;
    }

    public uint PartyId { get; }
    public uint InitiatorSessionId { get; }
    public ulong InitiatorGid { get; }

    /// <summary>True once the check has settled; later votes and settles do nothing.</summary>
    public bool Finished { get; private set; }

    /// <summary>
    /// Opens the check. A check already running for the party refuses it; otherwise the initiator is acknowledged and
    /// every other online member is told to open the dialog.
    /// </summary>
    public IReadOnlyList<ReadyCheckEffect> Start(bool alreadyChecking, IReadOnlyList<uint> memberSessionIds, Func<uint, bool> isOnline)
    {
        if (alreadyChecking) return [new AlreadyChecking()];
        var effects = new List<ReadyCheckEffect> { new AckOk() };
        foreach (var memberId in MembersToNotify(memberSessionIds, isOnline, InitiatorSessionId))
            effects.Add(new NotifyStart(memberId, InitiatorGid));
        return effects;
    }

    /// <summary>
    /// Records a vote. Nothing once settled, for an unknown voter or for the initiator; <see cref="AlreadyVoted"/> for a
    /// second vote (the first stands); otherwise <see cref="AckOk"/>.
    /// </summary>
    public IReadOnlyList<ReadyCheckEffect> Vote(ulong voterGid, byte vote)
    {
        if (Finished || voterGid == 0 || voterGid == InitiatorGid) return [];
        return _votes.TryAdd(voterGid, vote) ? [new AckOk()] : [new AlreadyVoted()];
    }

    /// <summary>
    /// Settles the check once every answerable member has voted or <paramref name="timedOut"/>. Idempotent: a settled
    /// check, or one still waiting on a member, returns nothing. The result is reported only while the initiator is
    /// still in the party (<paramref name="initiatorPresent"/>), otherwise the check is <see cref="Abandoned"/>.
    /// </summary>
    public IReadOnlyList<ReadyCheckEffect> Settle(IReadOnlyList<RosterEntry> roster, bool timedOut, bool initiatorPresent)
    {
        if (Finished) return [];
        if (!timedOut && roster.Any(m => m.Gid != InitiatorGid && m.CanAnswer && !_votes.ContainsKey(m.Gid))) return [];
        Finished = true;
        if (!initiatorPresent) return [new Abandoned()];
        var (ready, notReady, names) = Tally(roster, InitiatorGid, _votes);
        return [new CheckResult(ready, notReady, names)];
    }

    /// <summary>
    /// Returns the members who receive the 2699 notification: every online roster member except the initiator.
    /// </summary>
    public static List<uint> MembersToNotify(IReadOnlyList<uint> memberSessionIds, Func<uint, bool> isOnline,
        uint initiatorSessionId)
    {
        var to = new List<uint>();
        foreach (var memberId in memberSessionIds)
            if (memberId != initiatorSessionId && isOnline(memberId))
                to.Add(memberId);
        return to;
    }

    /// <summary>
    /// Counts the ready and not-ready votes and collects the names of the members who are not ready. The
    /// initiator is excluded.
    /// </summary>
    public static (byte Ready, byte NotReady, List<string> NotReadyNames) Tally(IReadOnlyList<RosterEntry> roster,
        ulong initiatorGid, IReadOnlyDictionary<ulong, byte> votes)
    {
        int ready = 0;
        var names = new List<string>();
        foreach (var member in roster)
        {
            if (member.Gid == initiatorGid) continue;
            if (votes.TryGetValue(member.Gid, out var v) && v == VoteReady) ready++;
            else names.Add(member.Name);
        }
        return ((byte)Math.Min(ready, byte.MaxValue), (byte)Math.Min(names.Count, byte.MaxValue), names);
    }
}
