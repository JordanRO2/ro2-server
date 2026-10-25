using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Protocol;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Makes NPCs speak their Npc_Dlg lines through the 2729 NfyNpcChat bubble. Two triggers apply: the combat line
/// when a monster enters combat, and idle chatter while a player stands nearby.
/// </summary>
/// <remarks>
/// <para>
/// The line id is <c>templatePrefix·10000 + situation·100 + index</c>, where the prefix is the NPC template id
/// divided by 10000. Lines come from the game data (<see cref="GameDataStore.NpcDlg"/>), so no line text is
/// defined here.
/// </para>
/// <para>
/// Variant templates (NPCInfo ids whose remainder modulo 10000 is not 0) do not follow the prefix rule. Their
/// combat line is one fixed row per asset, taken from <see cref="NpcEngageLines"/>.
/// </para>
/// <para>
/// Not covered: situations 03 to 06 (skill casts) and situations 10 and above (scripted boss lines), because the
/// server has no NPC skill-cast or boss-script system.
/// </para>
/// </remarks>
public sealed class NpcChatService : ITickable
{
 /// <summary>Npc_Dlg situation for idle chatter.</summary>
    internal const uint SituationIdle = 1;

 /// <summary>Npc_Dlg situation for the line spoken on entering combat.</summary>
    internal const uint SituationEnterCombat = 2;

 /// <summary>
 /// Chance that a base template with a combat row speaks it when an engagement opens.
 /// </summary>
    internal const double BaseCombatLineChance = 62.0 / 124.0;

 /// <summary>
 /// Template prefixes excluded from the line index because they follow a script rather than a random choice.
 /// </summary>
    internal static readonly FrozenSet<uint> ScriptedPrefixes = new uint[] { 9314, 9315 }.ToFrozenSet();

 /// <summary>
 /// Distance, in world units, within which a player makes an NPC speak.
 /// </summary>
 /// <remarks>
 /// Measured against <see cref="Models.PlayerState.X"/> and <see cref="NpcRuntimeState.PosX"/>. Beyond this distance
 /// NPCs almost never speak, so distance matters more than being in view.
 /// </remarks>
    internal const float IdleChatRadius = 30f;

 /// <summary>Chance per one-second pass that an NPC in range says its first idle line.</summary>
    internal const double FirstIdleLineChance = 0.079;

 /// <summary>Chance per one-second pass that an NPC says a later idle line, once the cooldown has passed.</summary>
    internal const double NextIdleLineChance = 0.138;

 /// <summary>Minimum number of passes between two idle lines from the same NPC.</summary>
    internal const long MinPassesBetweenLines = 2;

 /// <summary>Period of the idle pass.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

 /// <summary>
 /// Delay between the end of an engagement the monster survived and the state change that returns it to idle.
 /// </summary>
    internal static readonly TimeSpan ReturnToIdleDelay = TimeSpan.FromMilliseconds(322);

    private readonly GameDataStore _data;
    private readonly NpcStateManager _npcState;
    private readonly PlayerStateManager _players;
    private readonly BroadcastService _broadcast;
    private readonly ILogger<NpcChatService> _logger;

 /// <summary>
 /// Handles of NPCs currently in combat. An NPC in this set has already spoken its combat line for this engagement
 /// and says no idle lines.
 /// </summary>
    private readonly ConcurrentDictionary<uint, byte> _inCombat = new();

 /// <summary>
 /// NPC handle to the pass that owes it a return to idle. Cleared when a new engagement starts or the NPC dies or is
 /// released, so a delayed state change only reaches a monster nothing else has updated meanwhile.
 /// </summary>
    private readonly ConcurrentDictionary<uint, long> _returning = new();

 /// <summary>
 /// NPC handle to the pass number of its last idle line. A missing entry means the next line is the first one, since
 /// the NPC has not spoken since it came into range.
 /// </summary>
    private readonly ConcurrentDictionary<uint, long> _lastLinePass = new();

 /// <summary>
 /// NPC handle to the idle row its current chain speaks, for templates with several idle rows. The row is chosen at
 /// the start of the chain and kept until the chain ends (<see cref="EndIdleChain"/>).
 /// </summary>
    private readonly ConcurrentDictionary<uint, uint> _idleRow = new();

    private LineIndex? _index;
    private long _pass;
    private double _accum;
    private int _running;

    public string Name => "NpcChat";

    public NpcChatService(GameDataStore data, NpcStateManager npcState, PlayerStateManager players,
        BroadcastService broadcast, ILogger<NpcChatService> logger)
    {
        _data = data;
        _npcState = npcState;
        _players = players;
        _broadcast = broadcast;
        _logger = logger;
    }

 /// <summary>
 /// Handles a hit on an NPC. When the hit starts an engagement, it broadcasts the enter-combat state (2398 state 3,
 /// naming the attacker) and, by a fixed chance, the NPC's combat line. A hit within an engagement that is already
 /// open sends nothing.
 /// </summary>
 /// <param name="npc">The monster that was hit.</param>
 /// <param name="attackerHandle">Handle of the player whose hit this is.</param>
 /// <remarks>
 /// <para>
 /// Every damage path that can open an engagement must call this after sending its own messages.
 /// </para>
 /// </remarks>
    public async Task EnterCombatAsync(NpcRuntimeState npc, uint attackerHandle)
    {
        if (!_inCombat.TryAdd(npc.EntityOid, 0))
            return;                                    
        _returning.TryRemove(npc.EntityOid, out _);    

        var state = CombatPackets.BuildChangeState2398(npc.EntityOid,
            CombatPackets.ActorState.NpcEnterCombat, targetHandle: attackerHandle, trailing: 3, combatFlag: 1);
        await _broadcast.BroadcastToMapAsync(npc.MapId, ZoneS2COpcodes.OnChangeState,
            w => w.WriteBytes(state));

        EndIdleChain(npc.EntityOid);                   

        if (TryPickCombatLine(npc.NpcId, _data.Npcs.ContainsKey(npc.NpcId), GetIndex(), NpcEngageLines.Shipped,
                Random.Shared.NextDouble(), Random.Shared.NextDouble(), out var lineId))
            await BroadcastLineAsync(npc, lineId);
    }

 /// <summary>
 /// Returns the combat line an NPC speaks on entering combat, or false when it speaks none.
 /// </summary>
 /// <param name="npcId">The NPC's NPCInfo id.</param>
 /// <param name="clientKnowsNpc">Whether the asset has an NPCInfo row. The client cannot build an actor without one.</param>
 /// <param name="index">Index of the base templates' idle and combat rows.</param>
 /// <param name="engage">Fixed combat lines of variant assets, or null when none are loaded.</param>
    internal static bool TryPickCombatLine(uint npcId, bool clientKnowsNpc, LineIndex index, NpcEngageLines? engage,
        double draw, double rowDraw, out uint lineId)
    {
        lineId = 0;
        if (!clientKnowsNpc)
            return false;
        if (npcId % 10000 != 0)
        {
            if (engage is null || !engage.TryGet(npcId, out var row) || draw >= row.Chance)
                return false;
            lineId = row.LineId;
            return true;
        }
        if (!index.Combat.TryGetValue(npcId / 10000, out var rows) || draw >= BaseCombatLineChance)
            return false;
        lineId = PickRow(rows, rowDraw);
        return true;
    }

 /// <summary>Picks a row from <paramref name="rows"/> according to <paramref name="draw"/> in [0, 1).</summary>
    internal static uint PickRow(uint[] rows, double draw)
        => rows[Math.Clamp((int)(draw * rows.Length), 0, rows.Length - 1)];

 /// <summary>
 /// Called when an NPC leaves combat through a path that sends its own frames: a kill, or the release of monsters.
 /// The next hit then starts a new engagement. Engagements that end otherwise are closed by the idle pass.
 /// </summary>
    public void OnNpcLeftCombat(uint npcHandle)
    {
        _inCombat.TryRemove(npcHandle, out _);
        _returning.TryRemove(npcHandle, out _);
        EndIdleChain(npcHandle);
    }

 /// <summary>Ends an NPC's idle chain, so its next idle line is a first line with a fresh row.</summary>
    private void EndIdleChain(uint npcHandle)
    {
        _lastLinePass.TryRemove(npcHandle, out _);
        _idleRow.TryRemove(npcHandle, out _);
    }

 /// <summary>
 /// Called by the central game loop. Advances simulated time and runs one pass every <see cref="Interval"/>. The pass
 /// broadcasts, so it runs fire-and-forget off the loop thread, and passes never overlap.
 /// </summary>
    public void Tick(double deltaSeconds)
    {
        _accum += deltaSeconds;
        double interval = Interval.TotalSeconds;
        if (_accum < interval) return;

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            if (_accum > interval * 4) _accum = interval * 4;
            return;
        }
        
        
        _accum -= interval;
        if (_accum >= interval) _accum = 0;
        _ = RunPassAsync();
    }

    private async Task RunPassAsync()
    {
        try
        {
            await TickPassAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NPC chat pass failed");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

 /// <summary>
 /// Runs one pass. It first closes finished engagements, then gives each eligible NPC one chance to speak an idle line.
 /// </summary>
 /// <remarks>
 /// <para>
 /// An NPC is eligible when it is alive, the client can build it, it is a base template with an idle row, it is not
 /// in combat, and a player on its map is within <see cref="IdleChatRadius"/>. An NPC with a live threat target counts
 /// as in combat.
 /// </para>
 /// </remarks>
    private async Task TickPassAsync()
    {
        var pass = Interlocked.Increment(ref _pass);
        var index = GetIndex();

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        var inCombat = _inCombat.Keys.ToArray();
        var players = _players.GetAllPlayers().ToArray();
        var engaged = new HashSet<uint>();
        foreach (var p in players)
            if (p.IsInCombat && p.TargetEntityId != 0 && !p.IsDead)
                engaged.Add(p.TargetEntityId);
        var returning = new List<NpcRuntimeState>();
        foreach (var oid in inCombat)
        {
            var npc = _npcState.GetNpc(oid);
            if (npc is null || npc.IsDead)
                OnNpcLeftCombat(oid);
            else if (!engaged.Contains(oid) && !HasLiveTarget(npc) && _inCombat.TryRemove(oid, out _))
            {
                
                
                EndIdleChain(oid);
                _returning[oid] = pass;
                returning.Add(npc);
                await BroadcastStateAsync(npc, CombatPackets.ActorState.NpcReturn);
            }
        }

        
        
        var finishReturns = returning.Count == 0 ? Task.CompletedTask : FinishReturnsAsync(returning, pass);
        try
        {
            await IdlePassAsync(pass, index, players, engaged);
        }
        finally
        {
            await finishReturns;
        }
    }

 /// <summary>
 /// Sends the delayed return-to-idle state for each monster in <paramref name="batch"/> that is still alive and that
 /// nothing else has updated since the engagement ended.
 /// </summary>
    private async Task FinishReturnsAsync(List<NpcRuntimeState> batch, long pass)
    {
        await Task.Delay(ReturnToIdleDelay);
        foreach (var npc in batch)
        {
            if (!_returning.TryRemove(new KeyValuePair<uint, long>(npc.EntityOid, pass)))
                continue;
            if (npc.IsDead || !ReferenceEquals(_npcState.GetNpc(npc.EntityOid), npc))
                continue;
            await BroadcastStateAsync(npc, CombatPackets.ActorState.NpcLeaveCombat);
        }
    }

 /// <summary>Broadcasts a state change for a monster whose engagement has ended.</summary>
    private Task BroadcastStateAsync(NpcRuntimeState npc, byte state)
    {
        var body = CombatPackets.BuildChangeState2398(npc.EntityOid, state, trailing: 3);
        return _broadcast.BroadcastToMapAsync(npc.MapId, ZoneS2COpcodes.OnChangeState, w => w.WriteBytes(body));
    }

 /// <summary>The idle-chatter part of <see cref="TickPassAsync"/>.</summary>
    private async Task IdlePassAsync(long pass, LineIndex index, Models.PlayerState[] players, HashSet<uint> engaged)
    {
        
        
        var populated = players.Select(p => p.MapId).ToHashSet();
        foreach (var oid in _lastLinePass.Keys)
        {
            var npc = _npcState.GetNpc(oid);
            if (npc is null || npc.IsDead || !populated.Contains(npc.MapId))
                EndIdleChain(oid);
        }

        if (index.Idle.Count == 0)
            return;

        
        foreach (var map in players.GroupBy(p => p.MapId))
        {
            var positions = map.Select(p => (p.X, p.Y)).ToArray();
            foreach (var npc in _npcState.GetAliveNpcs(map.Key))
            {
                if (!TryGetIdleRows(npc, index, out var rows))
                    continue;
                
                
                
                
                
                if (_inCombat.ContainsKey(npc.EntityOid) || engaged.Contains(npc.EntityOid)
                    || npc.AggroTargetHandle != 0)
                    continue;

                if (!AnyWithin(positions, npc.PosX, npc.PosY, IdleChatRadius))
                {
                    EndIdleChain(npc.EntityOid);       
                    continue;
                }

                double chance;
                bool first = !_lastLinePass.TryGetValue(npc.EntityOid, out var last);
                if (!first)
                {
                    if (pass - last < MinPassesBetweenLines)
                        continue;
                    chance = NextIdleLineChance;
                }
                else
                {
                    chance = FirstIdleLineChance;
                }

                if (Random.Shared.NextDouble() >= chance)
                    continue;

                
                uint lineId = first
                    ? (_idleRow[npc.EntityOid] = PickRow(rows, Random.Shared.NextDouble()))
                    : _idleRow.GetOrAdd(npc.EntityOid, _ => PickRow(rows, Random.Shared.NextDouble()));
                _lastLinePass[npc.EntityOid] = pass;
                await BroadcastLineAsync(npc, lineId);
            }
        }
    }

 /// <summary>
 /// Whether the monster's current threat target is a live player on the monster's map.
 /// </summary>
    private bool HasLiveTarget(NpcRuntimeState npc)
    {
        var handle = npc.AggroTargetHandle;
        if (handle == 0)
            return false;
        var target = _players.GetPlayer(handle >> 8);
        return target is not null && target.EntityId == handle && !target.IsDead && target.MapId == npc.MapId;
    }

    private static bool AnyWithin((float X, float Y)[] positions, float x, float y, float radius)
    {
        var r2 = radius * radius;
        foreach (var (px, py) in positions)
        {
            var dx = x - px;
            var dy = y - py;
            if (dx * dx + dy * dy <= r2)
                return true;
        }
        return false;
    }

 /// <summary>
 /// Returns the idle rows an NPC can speak. False for an NPC the client cannot build, a variant template, or a template
 /// without an idle row.
 /// </summary>
    private bool TryGetIdleRows(NpcRuntimeState npc, LineIndex index, out uint[] rows)
    {
        rows = [];
        if (npc.NpcId % 10000 != 0 || !_data.Npcs.ContainsKey(npc.NpcId)
            || !index.Idle.TryGetValue(npc.NpcId / 10000, out var found))
            return false;
        rows = found;
        return true;
    }

 /// <summary>Broadcasts a bubble line as a 2729 NfyNpcChat frame on the internal notify path.</summary>
    private Task BroadcastLineAsync(NpcRuntimeState npc, uint lineId)
    {
        var body = NpcFuncObjNotifyPackets.BuildNfyNpcChat2729(npc.EntityOid, lineId);
        return _broadcast.BroadcastToMapAsync(npc.MapId, ZoneS2COpcodes.NfyNpcChat,
            w => w.WriteBytes(body));
    }

 /// <summary>
 /// Returns the (prefix to rows) index, built once from <see cref="GameDataStore.NpcDlg"/>. The index is not cached
 /// until the game data has loaded, so an early call cannot cache an empty index.
 /// </summary>
    private LineIndex GetIndex()
    {
        var idx = Volatile.Read(ref _index);
        if (idx is not null)
            return idx;
        if (_data.NpcDlg.Count == 0)
            return LineIndex.Empty;

        idx = BuildIndex(_data.NpcDlg.Select(r => r.Text_StringID));
        if (Interlocked.CompareExchange(ref _index, idx, null) is null)
        {
            var engage = NpcEngageLines.Shipped;
            _logger.LogInformation(
                "NPC chat lines indexed: {Idle} idle (01) and {Combat} combat (02) templates "
                + "({Multi} with several rows); {Engage} variant engage lines from {Path}",
                idx.Idle.Count, idx.Combat.Count,
                idx.Idle.Values.Count(r => r.Length > 1) + idx.Combat.Values.Count(r => r.Length > 1),
                engage?.Count ?? 0, NpcEngageLines.ShippedPath);
        }
        return Volatile.Read(ref _index)!;
    }

 /// <summary>
 /// Groups Npc_Dlg ids by template prefix and situation (idle and combat), keeping every row of a group in sorted
 /// order and leaving out <see cref="ScriptedPrefixes"/>.
 /// </summary>
    internal static LineIndex BuildIndex(IEnumerable<uint> textIds)
    {
        var idle = new Dictionary<uint, List<uint>>();
        var combat = new Dictionary<uint, List<uint>>();
        foreach (var id in textIds)
        {
            uint prefix = id / 10000, situation = id / 100 % 100;
            if (ScriptedPrefixes.Contains(prefix))
                continue;
            var table = situation == SituationIdle ? idle : situation == SituationEnterCombat ? combat : null;
            if (table is null)
                continue;
            if (!table.TryGetValue(prefix, out var ids))
                table[prefix] = ids = new List<uint>();
            if (!ids.Contains(id))
                ids.Add(id);
        }
        return new LineIndex(Freeze(idle), Freeze(combat));

        static FrozenDictionary<uint, uint[]> Freeze(Dictionary<uint, List<uint>> d)
            => d.ToFrozenDictionary(kv => kv.Key, kv => kv.Value.Order().ToArray());
    }

 /// <summary>Template prefix to its idle and combat rows.</summary>
    internal sealed record LineIndex(FrozenDictionary<uint, uint[]> Idle, FrozenDictionary<uint, uint[]> Combat)
    {
        public static readonly LineIndex Empty =
            new(FrozenDictionary<uint, uint[]>.Empty, FrozenDictionary<uint, uint[]>.Empty);
    }
}

/// <summary>
/// Combat lines of template-variant NPCs (NPCInfo ids whose remainder modulo 10000 is not 0), as shipped in
/// <see cref="ShippedPath"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each variant asset has one fixed combat line, whose id prefix does not match the asset. The table is loaded from
/// data/npc/NpcEngageLines.json.
/// </para>
/// </remarks>
public sealed class NpcEngageLines
{
 /// <summary>Path of the shipped table, relative to the working directory like the other data tables.</summary>
    public static readonly string ShippedPath = Path.Combine("data", "npc", "NpcEngageLines.json");

 /// <summary>One asset: its combat line and how often it was spoken on entering combat.</summary>
    public readonly record struct Row(uint Asset, uint LineId, int Engagements, int Spoken)
    {
 /// <summary>Fraction of engagements in which the line was spoken.</summary>
        public double Chance => Engagements > 0 ? (double)Spoken / Engagements : 0;
    }

    private readonly FrozenDictionary<uint, Row> _byAsset;

    private NpcEngageLines(IEnumerable<Row> rows) => _byAsset = rows.ToFrozenDictionary(r => r.Asset);

 /// <summary>Number of assets in the table.</summary>
    public int Count => _byAsset.Count;

 /// <summary>The rows of all assets.</summary>
    public IEnumerable<Row> Rows => _byAsset.Values;

 /// <summary>Returns the row of <paramref name="asset"/>.</summary>
    public bool TryGet(uint asset, out Row row) => _byAsset.TryGetValue(asset, out row);

    private static readonly Lazy<NpcEngageLines?> s_shipped = new(() => TryLoad(ShippedPath));

 /// <summary>The shipped table, or null when the file is missing or unreadable.</summary>
    public static NpcEngageLines? Shipped => s_shipped.Value;

 /// <summary>Loads a table from <paramref name="path"/>, or returns null on any failure.</summary>
    public static NpcEngageLines? TryLoad(string path)
    {
        try { return File.Exists(path) ? Load(path) : null; }
        catch { return null; }
    }

 /// <summary>Reads a table file. Throws if the file is malformed or contains duplicate or inconsistent entries.</summary>
    public static NpcEngageLines Load(string path)
    {
        var file = JsonSerializer.Deserialize<EngageFile>(File.ReadAllBytes(path))
                   ?? throw new InvalidDataException(path + " is empty");
        var rows = new List<Row>(file.Rows.Count);
        var seen = new HashSet<uint>();
        foreach (var r in file.Rows)
        {
            if (r.Asset % 10000 == 0)
                throw new InvalidDataException($"engage line asset {r.Asset} is a base template, not a variant");
            if (r.Engagements <= 0 || r.Spoken < 0 || r.Spoken > r.Engagements)
                throw new InvalidDataException($"engage line asset {r.Asset}: spoken {r.Spoken} of {r.Engagements}");
            if (!seen.Add(r.Asset))
                throw new InvalidDataException($"engage line asset {r.Asset} is listed twice");
            rows.Add(new Row(r.Asset, r.LineId, r.Engagements, r.Spoken));
        }
        return new NpcEngageLines(rows);
    }

    private sealed class EngageFile
    {
        [JsonPropertyName("rows")] public List<RowFile> Rows { get; set; } = new();
    }

    private sealed class RowFile
    {
        [JsonPropertyName("asset")] public uint Asset { get; set; }
        [JsonPropertyName("lineId")] public uint LineId { get; set; }
        [JsonPropertyName("engagements")] public int Engagements { get; set; }
        [JsonPropertyName("spoken")] public int Spoken { get; set; }
    }
}
