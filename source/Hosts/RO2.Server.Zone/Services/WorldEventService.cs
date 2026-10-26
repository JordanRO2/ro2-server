using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.GameData.Tables;
using RO2.Network.Protocol;
using RO2.Worlds.Application;
using RO2.Worlds.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// World-event stage rules over the persisted <see cref="WorldEventStateDocument"/>. The rules themselves are the pure
/// <see cref="WorldEventStageRules"/> in World.Domain. This host delegator keeps the document-based signatures and applies
/// the one in-place change (<see cref="AddOffering"/>) to the document.
/// </summary>
public static class WorldEventMachine
{
 /// <summary>A new state for an event entering a stage at the given time.</summary>
    public static WorldEventStateDocument Enter(uint eventId, byte stage, DateTime nowUtc, WorldEventTables t)
        => ToDocument(WorldEventStageRules.Enter(eventId, stage, nowUtc, t));

 /// <summary>
 /// The stored state adjusted to the tables, with one count per offering row of its stage, or null when its stage no longer exists.
 /// </summary>
    public static WorldEventStateDocument? Normalize(WorldEventStateDocument stored, WorldEventTables t)
        => WorldEventStageRules.Normalize(ToState(stored), t) is { } state ? ToDocument(state) : null;

 /// <summary>Result of an offering: whether it counted, and the stage it completed, if any.</summary>
    public readonly record struct OfferingResult(bool Counted, byte? NextStage);

 /// <summary>
 /// Counts an offering of items to an NPC toward the current stage. Changes <paramref name="doc"/> in place.
 /// </summary>
    public static OfferingResult AddOffering(WorldEventStateDocument doc, WorldEventTables t, uint npcTemplate,
        uint itemTemplate, int count)
    {
        var result = WorldEventStageRules.AddOffering(ToState(doc), t, npcTemplate, itemTemplate, count);
        if (!result.Counted) return default;
        doc.Counts.Clear();
        doc.Counts.AddRange(result.Counts);
        return new OfferingResult(true, result.NextStage);
    }

 /// <summary>When the current stage times out, or null when it does not.</summary>
    public static DateTime? Deadline(WorldEventStateDocument doc, WorldEventTables t)
        => WorldEventStageRules.Deadline(ToState(doc), t);

 /// <summary>The return stage the event falls back to at the given time, or null while the stage is still running.</summary>
    public static byte? TimedOut(WorldEventStateDocument doc, WorldEventTables t, DateTime nowUtc)
        => WorldEventStageRules.TimedOut(ToState(doc), t, nowUtc);

    internal static WorldEventState ToState(WorldEventStateDocument doc)
        => new(doc.Id, doc.Stage, doc.StageEnteredUtc, doc.Counts, doc.UpdatedUtc);

    internal static WorldEventStateDocument ToDocument(WorldEventState state) => new()
    {
        Id = state.Id,
        Stage = state.Stage,
        StageEnteredUtc = state.StageEnteredUtc,
        Counts = new List<uint>(state.Counts),
        UpdatedUtc = state.UpdatedUtc,
    };
}

/// <summary>
/// Server-wide world events: their live stage, the offerings that advance them, the stage timer, and the notices sent to players.
/// </summary>
/// <remarks>
/// The same instance is the singleton used by the map-load and offering handlers, and the hosted timer that checks stage timeouts.
/// State is kept in <see cref="IWorldEventStateRepository"/> and in memory, and each change is stored when it happens.
/// When the store cannot be read, the world-entry burst uses the initial stages and no offerings are counted until it can be read again.
/// </remarks>
public sealed class WorldEventService : BackgroundService
{
 /// <summary>Longest time the timer sleeps between deadline checks.</summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(1);

 /// <summary>How long a failed store load is not retried, so map loads do not each wait on an unavailable store.</summary>
    private static readonly TimeSpan LoadRetryDelay = TimeSpan.FromMinutes(1);

    private readonly GameDataStore _store;
    private readonly IWorldNotifier _notifier;
    private readonly IWorldEventStateRepository _repo;
    private readonly ILogger<WorldEventService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<uint, WorldEventStateDocument>? _states;
    private DateTime _nextLoadAttemptUtc = DateTime.MinValue;

    public WorldEventService(GameDataStore store, PlayerStateManager players, BroadcastService broadcast,
        IWorldEventStateRepository repo, ILogger<WorldEventService> logger)
    {
        _store = store;
        _notifier = new WorldEventNotifier(store, players, broadcast);
        _repo = repo;
        _logger = logger;
    }

 /// <summary>
 /// The 3030 bodies of the world-entry burst for a map, with each event at its current stage.
 /// </summary>
    public async Task<IReadOnlyList<byte[]>> BuildWorldEntryBurstAsync(uint mapId, CancellationToken ct)
    {
        var t = WorldEventTables.From(_store);
        IReadOnlyList<(uint Id, byte Stage)> stages;
        await _gate.WaitAsync(ct);
        try
        {
            var states = await LoadLockedAsync(ct);
            stages = WorldEventUseCase.EntryStages(states?.Values.Select(WorldEventMachine.ToState).ToList(), t);
        }
        finally
        {
            _gate.Release();
        }

        var events = stages
            .Select(s => (s.Id, s.Stage, t.Stage(s.Id, s.Stage) is { } row ? t.EventRows(row) : Array.Empty<WorldEventEventGroup>()))
            .ToList();
        return WorldEventPackets.BuildWorldEntryBurst(events, mapId);
    }

 /// <summary>
 /// Counts an accepted offering. When it completes the stage, moves the event to the next stage and notifies every online player.
 /// </summary>
    public async Task OnOfferingAsync(uint npcTemplate, uint itemTemplate, int count, CancellationToken ct)
    {
        var t = WorldEventTables.From(_store);
        (uint Id, byte Stage)? changed = null;
        await _gate.WaitAsync(ct);
        try
        {
            var states = await LoadLockedAsync(ct);
            if (states is null)
            {
                _logger.LogWarning("World events unreadable: offering of {Count}x {Item} to NPC {Npc} not counted",
                    count, itemTemplate, npcTemplate);
                return;
            }
            var decision = WorldEventUseCase.Offer(states.Values.Select(WorldEventMachine.ToState).ToList(), t,
                npcTemplate, itemTemplate, count, DateTime.UtcNow);
            if (decision is not null)
            {
                var current = decision.Previous;
                var next = WorldEventMachine.ToDocument(decision.Stored);
                var counts = string.Join("/", decision.CountsAfterOffering);
                if (decision.NextStage is { } stage)
                    changed = (current.Id, stage);
                states[current.Id] = next;
                await SaveAsync(next, ct);
                _logger.LogInformation("World event {Event} stage {Stage}: offering {Count}x {Item} to NPC {Npc} counted ({Counts}){Moved}",
                    current.Id, current.Stage, count, itemTemplate, npcTemplate, counts,
                    decision.NextStage is { } moved ? $" -> stage {moved}" : "");
            }
        }
        finally
        {
            _gate.Release();
        }

        if (changed is { } c)
            await PushStageChangeAsync(c.Id, c.Stage, ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var wait = MaxWait;
                try
                {
                    wait = await RunTimeoutsAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "World event timer pass failed");
                }
                await Task.Delay(wait, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            
        }
    }

 /// <summary>Moves every timed-out stage to its return stage, notifies players of each change, and returns how long to sleep.</summary>
    private async Task<TimeSpan> RunTimeoutsAsync(CancellationToken ct)
    {
        var t = WorldEventTables.From(_store);
        TimeSpan wait;
        var changes = new List<(uint Id, byte Stage)>();
        await _gate.WaitAsync(ct);
        try
        {
            var states = await LoadLockedAsync(ct);
            if (states is null) return MaxWait;
            var decision = WorldEventUseCase.RunTimeouts(states.Values.Select(WorldEventMachine.ToState).ToList(), t,
                DateTime.UtcNow, MaxWait);
            wait = decision.Wait;
            foreach (var transition in decision.Transitions)
            {
                var doc = WorldEventMachine.ToDocument(transition.Entered);
                states[doc.Id] = doc;
                await SaveAsync(doc, ct);
                changes.Add((doc.Id, doc.Stage));
                _logger.LogInformation("World event {Event}: stage {From} timed out -> stage {To}", doc.Id, transition.Previous.Stage, doc.Stage);
            }
        }
        finally
        {
            _gate.Release();
        }

        foreach (var (id, stage) in changes)
            await PushStageChangeAsync(id, stage, ct);
        return wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait;
    }

 /// <summary>
 /// Notifies every online player that an event moved to a stage. Players on a map of the stage receive its records for that map. Everyone else receives a stage-0 notice.
 /// </summary>
    private async Task PushStageChangeAsync(uint eventId, byte stage, CancellationToken ct)
    {
        var sent = await WorldEventUseCase.PushStageChangeAsync(_notifier, eventId, stage,
            (recipient, ex) => _logger.LogDebug(ex, "3030 stage push failed for session {Id}", recipient.SessionId), ct);
        _logger.LogInformation("World event {Event} now at stage {Stage}: 3030 pushed to {Count} player(s)", eventId, stage, sent);
    }

 /// <summary>The live event states, loaded from the store on first use. Null while the store cannot be read.</summary>
    private async Task<Dictionary<uint, WorldEventStateDocument>?> LoadLockedAsync(CancellationToken ct)
    {
        if (_states is not null) return _states;
        if (DateTime.UtcNow < _nextLoadAttemptUtc) return null;
        try
        {
            var t = WorldEventTables.From(_store);
            var stored = await _repo.GetAllAsync(ct);
            var now = DateTime.UtcNow;
            var states = new Dictionary<uint, WorldEventStateDocument>();
            foreach (var (id, initial) in WorldEventSelectors.InitialStages(t.Main))
            {
                var found = stored.FirstOrDefault(d => d.Id == id);
                var (state, needsStore) = WorldEventUseCase.Load(found is null ? null : WorldEventMachine.ToState(found), id, initial, now, t);
                var doc = WorldEventMachine.ToDocument(state);
                if (needsStore)
                    await _repo.SaveAsync(doc, ct);
                states[id] = doc;
            }
            _states = states;
            _logger.LogInformation("World events loaded: {States}",
                string.Join(", ", states.Values.OrderBy(s => s.Id).Select(s => $"{s.Id} stage {s.Stage}")));
            return states;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _nextLoadAttemptUtc = DateTime.UtcNow + LoadRetryDelay;
            _logger.LogWarning(ex, "World event state could not be loaded; retrying in {Delay}", LoadRetryDelay);
            return null;
        }
    }

    private async Task SaveAsync(WorldEventStateDocument doc, CancellationToken ct)
    {
        try
        {
            await _repo.SaveAsync(doc, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            
            _logger.LogWarning(ex, "World event {Event} stage {Stage} could not be stored", doc.Id, doc.Stage);
        }
    }
}

/// <summary>
/// Host adapter of <see cref="IWorldNotifier"/> over the live player registry, the broadcast service and the player
/// sessions. It builds the 3030 stage-change bodies and sends them with the same opcode and order as before.
/// </summary>
internal sealed class WorldEventNotifier : IWorldNotifier
{
    private readonly GameDataStore _store;
    private readonly PlayerStateManager _players;
    private readonly BroadcastService _broadcast;

    public WorldEventNotifier(GameDataStore store, PlayerStateManager players, BroadcastService broadcast)
    {
        _store = store;
        _players = players;
        _broadcast = broadcast;
    }

    public IReadOnlyList<WorldRecipient> OnlinePlayers()
        => _players.GetAllPlayers().Select(p => new WorldRecipient(p.SessionId, p.MapId)).ToList();

 /// <summary>
 /// Players on a map of the stage receive its records for that map. Everyone else receives a stage-0 notice.
 /// </summary>
    public async Task<bool> SendStageChangeAsync(WorldRecipient recipient, uint eventId, byte stage, CancellationToken ct)
    {
        var session = _broadcast.GetSession(recipient.SessionId);
        if (session is null) return false;
        var t = WorldEventTables.From(_store);
        var rows = t.Stage(eventId, stage) is { } row ? t.EventRows(row) : Array.Empty<WorldEventEventGroup>();
        foreach (var body in WorldEventPackets.BuildStageChangePush(eventId, stage, rows, recipient.MapId))
            await session.SendInternalRmiAsync(ZoneS2COpcodes.NfyWorldEventTrigger, body, ct);
        return true;
    }
}
