namespace RO2.Parties.Domain;

/// <summary>Which wire message an effect becomes; the host owns the opcode and the bytes.</summary>
public enum PartyMessageKind
{
    InviteResult,        // 2346 to the inviter (Code, Text = target name, Value = party type)
    InviteDialog,        // OnPartyJoin to the target (Gid = inviter, Value = party type, Text = inviter name)
    Error,               // 2348 to the responder (Code, Text = inviter name)
    Out,                 // 2360 (Gid = member, Value = 0 removed / 1 locked)
    LeaderChange,        // 2365 (Gid = new leader)
    Destroy,             // 2358
    ExpulsionAck,        // 2353 to the kicker (Code 0 = ok, 15712 = not a member)
    ExpulsionNotify,     // 2366 (Gid = expelled)
    ChangeMasterAck,     // OnPartyChangeMaster ack to the old leader
    AnsTypeChange,       // AnsPartyTypeChange (Value = type, Code 0 or 15719)
    NfyTypeChange,       // NfyPartyTypeChange (Value = type)
    AnsSubMaster,        // 2702
    NfySubMaster,        // Gid = promoted
    AnsDismissSubMaster, // 2706
    NfyDismissSubMaster, // Gid = dismissed
    AnsFrameMove,        // 1504 answer
    NfyFrameMove,        // 2705 (Gid/Value = moved + slot, Gid2/Value2 = occupant + slot)
    AnsHelperDeclare,    // 2902 (Gid, Value = flag)
}

/// <summary>The abstract content of one message; the host builds the bytes.</summary>
public readonly record struct PartyMessage(PartyMessageKind Kind, ulong Gid = 0, int Code = 0, string Text = "",
    byte Value = 0, ulong Gid2 = 0, byte Value2 = 0);

/// <summary>One send a decision queues, in send order. Recipients are online members only (the host skips offline ones).</summary>
public abstract record PartyEffect;

/// <summary>Send to one member.</summary>
public sealed record SendToMember(ulong Gid, PartyMessage Message) : PartyEffect;

/// <summary>Send to every online member of the party as it stands after the decision.</summary>
public sealed record SendToParty(PartyMessage Message) : PartyEffect;

/// <summary>Send to every online member except one.</summary>
public sealed record SendToPartyExcept(ulong ExceptGid, PartyMessage Message) : PartyEffect;

/// <summary>2357 roster: to every online member, or only to <see cref="OnlyGid"/> when set.</summary>
public sealed record SendRosterCreate(ulong? OnlyGid) : PartyEffect;

/// <summary>2361 roster to every online member.</summary>
public sealed record SendRosterUpdate : PartyEffect;

/// <summary>2359 delta for the joiner, to every online member except the joiner.</summary>
public sealed record SendJoinDelta(ulong JoinerGid) : PartyEffect;

/// <summary>2358 sent <see cref="Copies"/> times to each recipient (the survivors of a disbanded party).</summary>
public sealed record SendDestroy(int Copies, IReadOnlyList<ulong> Recipients) : PartyEffect
{
    public bool Equals(SendDestroy? other)
        => other is not null && Copies == other.Copies && Recipients.SequenceEqual(other.Recipients);

    public override int GetHashCode() => HashCode.Combine(Copies, Recipients.Count);
}

/// <summary>2367 layoff to a kicked member.</summary>
public sealed record SendLayoff(ulong Gid) : PartyEffect;

/// <summary>A member was locked: the host starts the expiry sweep timer unless it is already running.</summary>
public sealed record ScheduleLockSweep : PartyEffect;

public enum PartyResult
{
    Done,
    /// <summary>A party was founded by this decision.</summary>
    Created,
    /// <summary>The decision ended the party.</summary>
    Disbanded,
    /// <summary>Dropped silently: no state change, no effect.</summary>
    Ignored,
    /// <summary>Refused; <see cref="PartyOutcome.Code"/> is the refusal code.</summary>
    Refused,
    NotLeader,
    NotMember,
    NotEligible,
    InviteOpened,
    /// <summary>The invitation was consumed by the answer (the host removes it).</summary>
    InviteConsumed,
    /// <summary>An accept: the invitation is consumed and the host runs <c>DecideJoin</c> next.</summary>
    JoinNext,
    NoSuccessor,
}

/// <summary>What a decision concluded. <see cref="Party"/> is the party a join founded or joined; <see cref="Invite"/> the invitation to store.</summary>
public readonly record struct PartyOutcome(PartyResult Result, int Code = 0, PartyAggregate? Party = null,
    PartyInvite? Invite = null, ulong Gid = 0);
