namespace RO2.ReadyChecks.Domain;

/// <summary>What a ready check wants sent, in order. The host turns each effect into its frame.</summary>
public abstract record ReadyCheckEffect;

/// <summary>The requester's plain acknowledgement (the leader's 1500, a member's first 1501 vote).</summary>
public sealed record AckOk : ReadyCheckEffect;

/// <summary>A check is already running for the party: refuses the leader's 1500.</summary>
public sealed record AlreadyChecking : ReadyCheckEffect;

/// <summary>The member has already voted: refuses its repeated 1501.</summary>
public sealed record AlreadyVoted : ReadyCheckEffect;

/// <summary>Opens the "Battle Ready" dialog on <paramref name="ToSessionId"/>, naming the initiator by GID.</summary>
public sealed record NotifyStart(uint ToSessionId, ulong InitiatorGid) : ReadyCheckEffect;

/// <summary>The check settled: the outcome for the initiator, with the names of the members who are not ready.</summary>
public sealed record CheckResult(byte Ready, byte NotReady, IReadOnlyList<string> NotReadyNames) : ReadyCheckEffect;

/// <summary>The check settled but the initiator is no longer in the party, so no result is reported.</summary>
public sealed record Abandoned : ReadyCheckEffect;
