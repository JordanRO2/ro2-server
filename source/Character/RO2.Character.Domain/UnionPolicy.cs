namespace RO2.Characters.Domain;

/// <summary>Outcome of a union join request (1582).</summary>
public enum JoinUnionOutcome
{
    /// <summary>The join (or the already-joined same union) is accepted.</summary>
    Ok,
    /// <summary>The requested union id does not exist.</summary>
    UnknownUnion,
    /// <summary>The character already belongs to a different union.</summary>
    AlreadyJoined,
    /// <summary>The character's level is below the minimum to join.</summary>
    LevelTooLow,
}

/// <summary>Outcome of a union withdraw request (1583).</summary>
public enum WithdrawUnionOutcome
{
    /// <summary>The withdraw is accepted (or there was nothing to withdraw, which the client drops).</summary>
    Ok,
    /// <summary>The character is not registered to the requested union.</summary>
    NotInUnion,
    /// <summary>The 14-day withdraw lock since joining has not passed.</summary>
    TooSoon,
}

/// <summary>Pure union membership rules (join/withdraw), independent of the wire result codes (the host maps them).</summary>
public static class UnionPolicy
{
    /// <summary>How long after joining a character may leave: 14 days.</summary>
    public static readonly TimeSpan WithdrawLock = TimeSpan.FromDays(14);

    /// <summary>The lowest level that may join: 50.</summary>
    public const int JoinMinLevel = 50;

    /// <summary>
    /// The join decision, checked in order: an unknown union; the same union already stored (ok); a different union
    /// already joined; a level below the minimum; otherwise ok with a new membership.
    /// </summary>
    public static JoinUnionOutcome JoinResult(UnionMembership? current, uint requestedUnionId, bool unionExists, int level)
    {
        if (!unionExists) return JoinUnionOutcome.UnknownUnion;
        if (current is { } m) return m.UnionId == requestedUnionId ? JoinUnionOutcome.Ok : JoinUnionOutcome.AlreadyJoined;
        if (level < JoinMinLevel) return JoinUnionOutcome.LevelTooLow;
        return JoinUnionOutcome.Ok;
    }

    /// <summary>
    /// The withdraw decision: union id 0 or a different union means not in union; too soon within the lock;
    /// otherwise ok. A nonzero id with no stored membership is ok (the client drops it).
    /// </summary>
    public static WithdrawUnionOutcome WithdrawResult(UnionMembership? membership, uint requestedUnionId, long nowUnix)
    {
        if (requestedUnionId == 0) return WithdrawUnionOutcome.NotInUnion;
        if (membership is not { } m) return WithdrawUnionOutcome.Ok;
        if (m.UnionId != requestedUnionId) return WithdrawUnionOutcome.NotInUnion;
        if (nowUnix - m.JoinedAtUnix < (long)WithdrawLock.TotalSeconds) return WithdrawUnionOutcome.TooSoon;
        return WithdrawUnionOutcome.Ok;
    }
}
