using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Domain.GameData.Tables;
using RO2.Network.Protocol;

namespace RO2.Server.Zone.Services;

/// <summary>The three world-event tables the stage rules read.</summary>
public sealed record WorldEventTables(
    IReadOnlyList<WorldEventMain> Main,
    IReadOnlyList<WorldEventCheckGroup> Checks,
    IReadOnlyList<WorldEventEventGroup> Groups)
{
 /// <summary>Check type of the rows that require offering an item to an NPC.</summary>
    public const byte CheckTypeOffering = 2;

    public static WorldEventTables From(GameDataStore store)
        => new(store.WorldEventMain, store.WorldEventCheckGroup, store.WorldEventEventGroup);

 /// <summary>The main-table row of an event at the given stage.</summary>
    public WorldEventMain? Stage(uint eventId, byte stage)
        => Main.FirstOrDefault(r => r.World_Event_ID == eventId && r.Stage_Level == stage);

 /// <summary>The offering rows of the stage's first check group, in index order. Empty when the group id is 0.</summary>
    public IReadOnlyList<WorldEventCheckGroup> OfferingRows(WorldEventMain stage)
        => stage.Check_Group_ID_1 == 0
            ? Array.Empty<WorldEventCheckGroup>()
            : Checks.Where(c => c.Check_Group_ID == stage.Check_Group_ID_1 && c.Check_Type == CheckTypeOffering)
                .OrderBy(c => c.INDEX).ToList();

 /// <summary>The event-group rows of the stage, in table order.</summary>
    public IReadOnlyList<WorldEventEventGroup> EventRows(WorldEventMain stage)
        => Groups.Where(g => g.Event_Group_ID == stage.Event_Group_ID).ToList();
}

/// <summary>
/// World-event stage rules, free of storage and network access so they can be tested in isolation.
/// </summary>
/// <remarks>
/// Each stage completes when every offering row of its check group is met. Each row counts separately and the counts restart at each stage.
/// A stage that has a duration returns to its return stage when the duration elapses.
/// </remarks>
public static class WorldEventMachine
{
 /// <summary>A new state for an event entering a stage at the given time.</summary>
    public static WorldEventStateDocument Enter(uint eventId, byte stage, DateTime nowUtc, WorldEventTables t)
    {
        var row = t.Stage(eventId, stage);
        var rows = row is null ? 0 : t.OfferingRows(row).Count;
        return new WorldEventStateDocument
        {
            Id = eventId,
            Stage = stage,
            StageEnteredUtc = nowUtc,
            Counts = Enumerable.Repeat(0u, rows).ToList(),
            UpdatedUtc = nowUtc,
        };
    }

 /// <summary>
 /// The stored state adjusted to the tables, with one count per offering row of its stage, or null when its stage no longer exists.
 /// </summary>
    public static WorldEventStateDocument? Normalize(WorldEventStateDocument stored, WorldEventTables t)
    {
        var row = t.Stage(stored.Id, stored.Stage);
        if (row is null) return null;
        var doc = stored.Clone();
        var rows = t.OfferingRows(row).Count;
        if (doc.Counts.Count > rows) doc.Counts.RemoveRange(rows, doc.Counts.Count - rows);
        while (doc.Counts.Count < rows) doc.Counts.Add(0);
        return doc;
    }

 /// <summary>Result of an offering: whether it counted, and the stage it completed, if any.</summary>
    public readonly record struct OfferingResult(bool Counted, byte? NextStage);

 /// <summary>
 /// Counts an offering of items to an NPC toward the current stage. Changes <paramref name="doc"/> in place.
 /// </summary>
 /// <remarks>
 /// The offering counts only when the item and NPC pair is a row of the current stage's first check group.
 /// The next stage is reported once every row in the group has reached its required count.
 /// </remarks>
    public static OfferingResult AddOffering(WorldEventStateDocument doc, WorldEventTables t, uint npcTemplate,
        uint itemTemplate, int count)
    {
        if (count <= 0) return default;
        var row = t.Stage(doc.Id, doc.Stage);
        if (row is null) return default;
        var rows = t.OfferingRows(row);
        int i = -1;
        for (int k = 0; k < rows.Count; k++)
            if (rows[k].Type_Data_1 == npcTemplate && rows[k].Type_Data_2 == itemTemplate) { i = k; break; }
        if (i < 0) return default;

        while (doc.Counts.Count < rows.Count) doc.Counts.Add(0);
        doc.Counts[i] = (uint)Math.Min((ulong)doc.Counts[i] + (ulong)count, uint.MaxValue);

        for (int k = 0; k < rows.Count; k++)
            if (doc.Counts[k] < rows[k].Type_Data_3) return new OfferingResult(true, null);
        return t.Stage(doc.Id, row.Next_Stage) is null
            ? new OfferingResult(true, null)
            : new OfferingResult(true, row.Next_Stage);
    }

 /// <summary>When the current stage times out, or null when it does not.</summary>
    public static DateTime? Deadline(WorldEventStateDocument doc, WorldEventTables t)
        => t.Stage(doc.Id, doc.Stage) is { Stage_Duration_Time: > 0 } row
            ? doc.StageEnteredUtc.AddMinutes(row.Stage_Duration_Time)
            : null;

 /// <summary>The return stage the event falls back to at the given time, or null while the stage is still running.</summary>
    public static byte? TimedOut(WorldEventStateDocument doc, WorldEventTables t, DateTime nowUtc)
    {
        if (Deadline(doc, t) is not { } deadline || nowUtc < deadline) return null;
        var row = t.Stage(doc.Id, doc.Stage)!;
        return t.Stage(doc.Id, row.Return_Stage) is null ? null : row.Return_Stage;
    }
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
    private readonly PlayerStateManager _players;
    private readonly BroadcastService _broadcast;
    private readonly IWorldEventStateRepository _repo;
    private readonly ILogger<WorldEventService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<uint, WorldEventStateDocument>? _states;
    private DateTime _nextLoadAttemptUtc = DateTime.MinValue;

    public WorldEventService(GameDataStore store, PlayerStateManager players, BroadcastService broadcast,
        IWorldEventStateRepository repo, ILogger<WorldEventService> logger)
    {
        _store = store;
        _players = players;
        _broadcast = broadcast;
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
            stages = states is null
                ? WorldEventPackets.InitialStages(t.Main)
                : states.Values.OrderBy(s => s.Id).Select(s => (s.Id, s.Stage)).ToList();
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
            foreach (var current in states.Values.OrderBy(s => s.Id).ToList())
            {
                var next = current.Clone();
                var result = WorldEventMachine.AddOffering(next, t, npcTemplate, itemTemplate, count);
                if (!result.Counted) continue;
                var now = DateTime.UtcNow;
                next.UpdatedUtc = now;
                var counts = string.Join("/", next.Counts);
                if (result.NextStage is { } stage)
                {
                    next = WorldEventMachine.Enter(current.Id, stage, now, t);
                    changed = (current.Id, stage);
                }
                states[current.Id] = next;
                await SaveAsync(next, ct);
                _logger.LogInformation("World event {Event} stage {Stage}: offering {Count}x {Item} to NPC {Npc} counted ({Counts}){Moved}",
                    current.Id, current.Stage, count, itemTemplate, npcTemplate, counts,
                    result.NextStage is { } moved ? $" -> stage {moved}" : "");
                break; 
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
        var wait = MaxWait;
        var changes = new List<(uint Id, byte Stage)>();
        await _gate.WaitAsync(ct);
        try
        {
            var states = await LoadLockedAsync(ct);
            if (states is null) return MaxWait;
            var now = DateTime.UtcNow;
            foreach (var current in states.Values.OrderBy(s => s.Id).ToList())
            {
                var doc = current;
                if (WorldEventMachine.TimedOut(doc, t, now) is { } back)
                {
                    doc = WorldEventMachine.Enter(doc.Id, back, now, t);
                    states[doc.Id] = doc;
                    await SaveAsync(doc, ct);
                    changes.Add((doc.Id, back));
                    _logger.LogInformation("World event {Event}: stage {From} timed out -> stage {To}", doc.Id, current.Stage, back);
                }
                if (WorldEventMachine.Deadline(doc, t) is { } deadline && deadline - now < wait)
                    wait = deadline - now;
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
        var t = WorldEventTables.From(_store);
        var rows = t.Stage(eventId, stage) is { } row ? t.EventRows(row) : Array.Empty<WorldEventEventGroup>();
        var sent = 0;
        foreach (var player in _players.GetAllPlayers().ToList())
        {
            var session = _broadcast.GetSession(player.SessionId);
            if (session is null) continue;
            try
            {
                foreach (var body in WorldEventPackets.BuildStageChangePush(eventId, stage, rows, player.MapId))
                    await session.SendInternalRmiAsync(ZoneS2COpcodes.NfyWorldEventTrigger, body, ct);
                sent++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                
                _logger.LogDebug(ex, "3030 stage push failed for session {Id}", player.SessionId);
            }
        }
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
            foreach (var (id, initial) in WorldEventPackets.InitialStages(t.Main))
            {
                var doc = stored.FirstOrDefault(d => d.Id == id) is { } s ? WorldEventMachine.Normalize(s, t) : null;
                if (doc is null)
                {
                    doc = WorldEventMachine.Enter(id, initial, now, t);
                    await _repo.SaveAsync(doc, ct);
                }
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
