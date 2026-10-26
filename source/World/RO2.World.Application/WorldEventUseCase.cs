using RO2.GameData.Tables;
using RO2.Worlds.Domain;

namespace RO2.Worlds.Application;

/// <summary>The offering that counted: the state before it, the state to store, and the stage it completed, if any.</summary>
/// <param name="Previous">The event's state before the offering.</param>
/// <param name="Stored">The state to store: the counts after the offering stamped at the decision time, or, when the offering completed a stage, a fresh entry into the next stage.</param>
/// <param name="CountsAfterOffering">The stage's counts after the offering, before any move to the next stage.</param>
/// <param name="NextStage">The stage the offering completed, or null.</param>
public sealed record WorldEventOfferDecision(
    WorldEventState Previous, WorldEventState Stored, IReadOnlyList<uint> CountsAfterOffering, byte? NextStage);

/// <summary>One stage that timed out: its state before, and the fresh entry into its return stage.</summary>
public sealed record WorldEventStageTimeout(WorldEventState Previous, WorldEventState Entered);

/// <summary>The timeouts to apply, in ascending event id, and how long the timer may sleep before the next deadline.</summary>
/// <param name="Transitions">Every stage that timed out.</param>
/// <param name="Wait">The time to the nearest deadline over the states after the transitions, never above the longest wait given.</param>
public sealed record WorldEventTimeoutDecision(IReadOnlyList<WorldEventStageTimeout> Transitions, TimeSpan Wait);

/// <summary>
/// The decisions of the server-wide world events, over the pure <see cref="WorldEventStageRules"/>. Each decision is a
/// pure function of the live states and the tables, and the host applies it (store, in-memory state, log, push) in its
/// own order. The current time enters as a parameter.
/// </summary>
public static class WorldEventUseCase
{
    /// <summary>
    /// The (event, stage) pairs a world-entry burst carries: each live event at its stage in ascending event id, or, when
    /// the live states cannot be read (<paramref name="live"/> null), each event at its initial stage.
    /// </summary>
    public static IReadOnlyList<(uint Id, byte Stage)> EntryStages(IEnumerable<WorldEventState>? live, WorldEventTables t)
        => live is null
            ? WorldEventSelectors.InitialStages(t.Main)
            : live.OrderBy(s => s.Id).Select(s => (s.Id, s.Stage)).ToList();

    /// <summary>
    /// The state an event starts with when the live states are loaded: the stored state adjusted to the tables, or, when
    /// there is none or its stage no longer exists, a fresh entry into <paramref name="initialStage"/>, which the host must store.
    /// </summary>
    public static (WorldEventState State, bool NeedsStore) Load(
        WorldEventState? stored, uint eventId, byte initialStage, DateTime nowUtc, WorldEventTables t)
    {
        var state = stored is null ? null : WorldEventStageRules.Normalize(stored, t);
        return state is null
            ? (WorldEventStageRules.Enter(eventId, initialStage, nowUtc, t), true)
            : (state, false);
    }

    /// <summary>
    /// Counts an offering of <paramref name="count"/> items of <paramref name="itemTemplate"/> to <paramref name="npcTemplate"/>.
    /// Events are tried in ascending id and the first one the offering counts toward wins. Returns null when none counts.
    /// </summary>
    public static WorldEventOfferDecision? Offer(
        IEnumerable<WorldEventState> live, WorldEventTables t, uint npcTemplate, uint itemTemplate, int count, DateTime nowUtc)
    {
        foreach (var current in live.OrderBy(s => s.Id))
        {
            var result = WorldEventStageRules.AddOffering(current, t, npcTemplate, itemTemplate, count);
            if (!result.Counted) continue;
            var stored = current with { Counts = result.Counts, UpdatedUtc = nowUtc };
            if (result.NextStage is { } stage)
                stored = WorldEventStageRules.Enter(current.Id, stage, nowUtc, t);
            return new WorldEventOfferDecision(current, stored, result.Counts, result.NextStage);
        }
        return null;
    }

    /// <summary>
    /// Moves every timed-out stage to its return stage (ascending event id) and works out how long the timer may sleep:
    /// the time to the nearest deadline after the moves, capped at <paramref name="maxWait"/>. The host keeps its own floor
    /// on the sleep.
    /// </summary>
    public static WorldEventTimeoutDecision RunTimeouts(
        IEnumerable<WorldEventState> live, WorldEventTables t, DateTime nowUtc, TimeSpan maxWait)
    {
        var wait = maxWait;
        var transitions = new List<WorldEventStageTimeout>();
        foreach (var current in live.OrderBy(s => s.Id))
        {
            var state = current;
            if (WorldEventStageRules.TimedOut(state, t, nowUtc) is { } back)
            {
                state = WorldEventStageRules.Enter(state.Id, back, nowUtc, t);
                transitions.Add(new WorldEventStageTimeout(current, state));
            }
            if (WorldEventStageRules.Deadline(state, t) is { } deadline && deadline - nowUtc < wait)
                wait = deadline - nowUtc;
        }
        return new WorldEventTimeoutDecision(transitions, wait);
    }

    /// <summary>
    /// Sends the stage-change notice of an event to every online player through <paramref name="notifier"/>, and returns how
    /// many were sent. A failed send is reported to <paramref name="onSendFailed"/> and does not stop the others, but a
    /// cancellation the caller asked for does.
    /// </summary>
    public static async Task<int> PushStageChangeAsync(
        IWorldNotifier notifier, uint eventId, byte stage, Action<WorldRecipient, Exception> onSendFailed, CancellationToken ct)
    {
        var sent = 0;
        foreach (var recipient in notifier.OnlinePlayers())
        {
            try
            {
                if (!await notifier.SendStageChangeAsync(recipient, eventId, stage, ct)) continue;
                sent++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                onSendFailed(recipient, ex);
            }
        }
        return sent;
    }
}
