using RO2.Characters.Domain;

namespace RO2.Characters.Application;

/// <summary>Joins a character to a union (RMI 1582 ReqJoinUnion): decides the outcome and stores a new membership.</summary>
public static class JoinUnionFlow
{
    /// <summary>The outcome of a join and the membership that stands after it (new, pre-existing, or null on refusal).</summary>
    public readonly record struct JoinUnionResult(JoinUnionOutcome Outcome, UnionMembership? Stored);

    /// <summary>
    /// Reads the membership, applies <see cref="UnionPolicy.JoinResult"/>, and stores a new one when the join is
    /// accepted and none was held. The union-existence lookup runs first; the clock is read after the repository
    /// read and before the write. On a lost race the membership is re-read: the same union is still Ok (with the
    /// stored time), any other is <see cref="JoinUnionOutcome.AlreadyJoined"/>.
    /// </summary>
    /// <param name="repo">The membership repository.</param>
    /// <param name="data">Game data, for the union-existence check.</param>
    /// <param name="characterId">The joining character.</param>
    /// <param name="level">The character's level, for the minimum-level rule.</param>
    /// <param name="unionId">The requested union.</param>
    /// <param name="time">The clock, read for a new membership's join time.</param>
    /// <param name="ct">A cancellation token.</param>
    public static async Task<JoinUnionResult> JoinAsync(IUnionMembershipRepository repo, ICharacterGameData data,
        uint characterId, int level, uint unionId, TimeProvider time, CancellationToken ct = default)
    {
        bool unionExists = data.UnionExists(unionId);
        var current = await repo.GetAsync(characterId, ct);
        var outcome = UnionPolicy.JoinResult(current, unionId, unionExists, level);

        if (outcome == JoinUnionOutcome.Ok && current is null)
        {
            long now = time.GetUtcNow().ToUnixTimeSeconds();
            if (await repo.JoinAsync(characterId, unionId, now, ct))
                current = new UnionMembership(unionId, now);
            else
            {
                current = await repo.GetAsync(characterId, ct);
                outcome = current is { } stored && stored.UnionId == unionId
                    ? JoinUnionOutcome.Ok
                    : JoinUnionOutcome.AlreadyJoined;
            }
        }

        return new JoinUnionResult(outcome, current);
    }
}

/// <summary>Withdraws a character from a union (RMI 1583 ReqWithdrawUnion): decides, deletes, and clears the mirror.</summary>
public static class WithdrawUnionFlow
{
    /// <summary>The outcome, whether a membership row was deleted, and the membership held before the request.</summary>
    public readonly record struct WithdrawUnionResult(WithdrawUnionOutcome Outcome, bool Deleted, UnionMembership? Stored);

    /// <summary>
    /// Reads the membership, applies <see cref="UnionPolicy.WithdrawResult"/> (clock read after the read), deletes
    /// the row when the withdraw is accepted and a membership was held, and clears the published mirror whenever
    /// the outcome is Ok (including the lost-race and nothing-stored cases). It issues no reply; the host does.
    /// </summary>
    /// <param name="repo">The membership repository.</param>
    /// <param name="publisher">The mirror the character head is built from.</param>
    /// <param name="characterId">The leaving character.</param>
    /// <param name="unionId">The requested union.</param>
    /// <param name="time">The clock, read for the withdraw-lock check.</param>
    /// <param name="ct">A cancellation token.</param>
    public static async Task<WithdrawUnionResult> WithdrawAsync(IUnionMembershipRepository repo,
        IUnionMembershipPublisher publisher, uint characterId, uint unionId, TimeProvider time, CancellationToken ct = default)
    {
        var membership = await repo.GetAsync(characterId, ct);
        long now = time.GetUtcNow().ToUnixTimeSeconds();
        var outcome = UnionPolicy.WithdrawResult(membership, unionId, now);

        bool deleted = outcome == WithdrawUnionOutcome.Ok && membership is not null
            && await repo.WithdrawAsync(characterId, unionId, ct);

        if (outcome == WithdrawUnionOutcome.Ok)
            publisher.Publish(characterId, null);

        return new WithdrawUnionResult(outcome, deleted, membership);
    }
}

/// <summary>Re-reads a character's stored membership and republishes it to the head mirror (after a join).</summary>
public static class UnionMirrorRefresh
{
    /// <summary>Publishes the character's currently stored membership (or none) to <paramref name="publisher"/>.</summary>
    /// <param name="repo">The membership repository.</param>
    /// <param name="publisher">The mirror the character head is built from.</param>
    /// <param name="characterId">The character whose mirror is refreshed.</param>
    /// <param name="ct">A cancellation token.</param>
    public static async Task RefreshAsync(IUnionMembershipRepository repo, IUnionMembershipPublisher publisher,
        uint characterId, CancellationToken ct = default)
        => publisher.Publish(characterId, await repo.GetAsync(characterId, ct));
}
