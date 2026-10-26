using RO2.Characters.Domain;

namespace RO2.Characters.Application;

/// <summary>
/// Sets the character's "anyone may review me" flag (RMI 1697 ReqAllowAnyoneReviewMe). The flow mirrors the new
/// value onto the online snapshot, announces it to the client and nearby players, and only then persists it, so
/// a slow or failing write never delays the reply.
/// </summary>
public static class SetAllowAnyoneReviewFlow
{
    /// <summary>
    /// Mirrors <paramref name="allow"/> onto the session snapshot, runs <paramref name="announce"/> (the reply
    /// and the nearby broadcast), then persists the flag. Returns the exception the write swallowed, or null.
    /// </summary>
    /// <param name="repo">The character repository the setting is persisted to.</param>
    /// <param name="gateway">The live session whose snapshot is mirrored.</param>
    /// <param name="characterId">The character the setting belongs to.</param>
    /// <param name="allow">Whether other players may review this one.</param>
    /// <param name="announce">The host's reply-and-broadcast step, run after the mirror and before the write.</param>
    /// <param name="ct">A cancellation token for the persistence call.</param>
    public static async Task<Exception?> ExecuteAsync(ICharacterRepository repo, IPlayerSessionGateway gateway,
        uint characterId, bool allow, Func<Task> announce, CancellationToken ct = default)
    {
        if (gateway.Snapshot is { } snapshot)
            snapshot.AllowAnyoneReview = allow;

        await announce();

        try { await repo.UpdateAllowAnyoneReviewAsync(characterId, allow, ct); return null; }
        catch (Exception ex) { return ex; }
    }
}

/// <summary>
/// Sets the character's respawn bind point (RMI 1309 ReqSetBindIndex). The bind lives only in session state here;
/// it reaches the database later through the autosave path, so this flow issues no repository call.
/// </summary>
public static class BindPointFlow
{
    /// <summary>
    /// Records <paramref name="index"/> as the session's bind point and mirrors it onto the online snapshot (so
    /// the in-world head reports it at head+105). Returns the index that was set before this call.
    /// </summary>
    /// <param name="gateway">The live session whose bind index and snapshot are updated.</param>
    /// <param name="index">The new bind index, as sent by the client.</param>
    public static ushort Set(IPlayerSessionGateway gateway, ushort index)
    {
        var previous = gateway.BindIndex;
        gateway.BindIndex = index;
        if (gateway.Snapshot is { } snapshot)
            snapshot.BindIndex = index;
        return previous;
    }
}
