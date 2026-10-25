using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1660 (ReqTRoomIn). Registers the requester, or its party when it leads one, for the Training Room.
/// </summary>
/// <remarks>
/// <para>
/// The room's map and rules come from the data tables (<see cref="TrainingRoomQueue.RulesOf"/>). The body is empty.
/// </para>
/// <para>
/// A requester below the room's required level is refused with NfyEntryPartyError (2969) and not registered.
/// Otherwise the registration is kept, and on each 5-second tick the party receives its waiting state
/// (NfyTRoomPartyState, 2972) whenever the classes in the party have changed. A party at or above the required
/// size is registered but receives no state, because matching is not implemented on this server.
/// </para>
/// <para>
/// A member who is not the leader of an unregistered party is ignored. A repeated 1660 while registered sends the
/// waiting state again on the next tick.
/// </para>
/// </remarks>
public class TRoomInHandler : ZoneRmiHandlerBase<TRoomInHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqTRoomIn;
    public override string Name => "TRoomIn";
    public TRoomInHandler(IServiceScopeFactory s, ILogger<TRoomInHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var data = services.GetRequiredService<GameDataStore>();
        if (TrainingRoomQueue.RulesOf(data) is not { } rules)
        {
            Logger.LogWarning("{Name} asks to enter the Training Room, but TRoom_Info / Position_Info / Entry_control name no Training Room map: nothing is registered",
                player.Name);
            return;
        }

        if (player.Level < rules.NeedLevel)
        {
            Logger.LogInformation("{Name} (level {Level}) asks to enter the Training Room below Need_Level {Need}: 2969 code {Code}",
                player.Name, player.Level, rules.NeedLevel, TrainingRoomQueue.CodeLevelTooLow);
            await session.SendInternalRmiAsync(ZoneS2COpcodes.NfyEntryPartyError,
                EntryPartyErrorPackets.Build(TrainingRoomQueue.CodeLevelTooLow), ct);
            return;
        }

        var parties = services.GetRequiredService<PartyService>();
        var result = TrainingRoomQueue.Shared.Register(player, parties);
        if (result == TrainingRoomQueue.RegisterResult.NotLeader)
        {
            Logger.LogInformation("{Name} asks to enter the Training Room as a member of an unregistered party: nothing is registered or sent (only the leader's 1660 is on record)",
                player.Name);
            return;
        }
        TrainingRoomQueue.Shared.EnsureTicking(new TrainingRoomQueue.TickContext(PlayerManager, parties, Broadcast.GetSession, rules, Logger));
        Logger.LogInformation("{Name} {What} for the Training Room (map {Map}); the waiting state goes out on the next 5 s tick",
            player.Name, result == TrainingRoomQueue.RegisterResult.Registered ? "registers" : "is already registered", rules.MapId);
    }
}

/// <summary>
/// The Training Room registrations of this Zone process: one per registering player, standing for that player's
/// party when it leads one, and the 5-second tick that sends each waiting state.
/// </summary>
/// <remarks>
/// <para>
/// Ticks fall on multiples of <see cref="TickPeriod"/> of the UTC clock (see <see cref="UntilNextTick"/>). The tick
/// loop runs only while something is registered (<see cref="EnsureTicking"/>).
/// </para>
/// <para>
/// A registration ends on a 1661 (<see cref="Cancel"/>), or when the registrant is no longer in the world or on the
/// map it registered from. The check happens at the next tick.
/// </para>
/// </remarks>
public sealed class TrainingRoomQueue
{
 /// <summary>The registrations the Zone handlers share.</summary>
    public static TrainingRoomQueue Shared { get; } = new();

 /// <summary>The matchmaker's period (see the class remarks).</summary>
    public static readonly TimeSpan TickPeriod = TimeSpan.FromSeconds(5);

 /// <summary>2969 error code sent to a requester below the room's required level.</summary>
    public const ushort CodeLevelTooLow = 24336;

 /// <summary>
 /// The Training Room's rules from the tables: the map (Position_Info[TRoom_Info.Position_A].Area_ID, 4110),
 /// its Entry_control Party_Need_Cnt (5) and TRoom_Info.Need_Level (50).
 /// </summary>
    public sealed record Rules(uint MapId, byte PartyNeedCount, byte NeedLevel);

 /// <summary>What the tick needs, all of it the Zone's singletons (captured by the handler).</summary>
    public sealed record TickContext(PlayerStateManager Players, PartyService Parties,
        Func<uint, ProudSession?> SessionOf, Rules Rules, ILogger Logger);

 /// <summary>One send the tick makes: a 2972 body for one member's session.</summary>
    public readonly record struct Send(uint SessionId, byte[] Body);

 /// <summary>One registration.</summary>
    public sealed class Entry(PlayerState registrant, uint mapId)
    {
        public PlayerState Registrant { get; } = registrant;
        public uint SessionId { get; } = registrant.SessionId;

 /// <summary>The registrant's map when it registered; leaving it ends the registration.</summary>
        public uint MapId { get; } = mapId;

 /// <summary>The class vector last sent, or null when the next tick must send it.</summary>
        internal byte[]? Shown { get; set; }
    }

    private readonly Lock _gate = new();
    private readonly List<Entry> _entries = [];
    private TickContext? _context;
    private int _tickAlive; 

 /// <summary>
 /// Reads the rules from <paramref name="data"/>, or returns null when the tables define no Training Room. When the
 /// room table has several rows, the last one is used.
 /// </summary>
    public static Rules? RulesOf(GameDataStore data)
    {
        if (data.TRoomInfo.Count == 0) return null;
        var info = data.TRoomInfo[^1];
        if (!data.Positions.TryGetValue(info.Position_A, out var position)) return null;
        var control = data.EntryControl.FirstOrDefault(c => c.Map_ID == position.Area_ID);
        return control is null || control.Party_Need_Cnt == 0
            ? null
            : new Rules(control.Map_ID, control.Party_Need_Cnt, info.Need_Level);
    }

 /// <summary>Number of registrations.</summary>
    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

 /// <summary>What a 1660 did to the registrations.</summary>
    public enum RegisterResult
    {
 /// <summary>A new registration.</summary>
        Registered,

 /// <summary>The player, or the party it is in, already had one; the next tick sends its state again.</summary>
        AlreadyRegistered,

 /// <summary>A party member who is not its leader, for a party with no registration: nothing changed.</summary>
        NotLeader,
    }

 /// <summary>
 /// Registers <paramref name="player"/>: itself when in no party, its party when it leads one. The player's own
 /// registration from another map, not yet dropped by a tick, is replaced by one from this map.
 /// </summary>
    public RegisterResult Register(PlayerState player, PartyService parties)
    {
        var party = parties.GetPlayerParty(player.SessionId);
        lock (_gate)
        {
            var existing = FindLocked(player, party?.PartyId, parties);
            if (existing is not null && !(ReferenceEquals(existing.Registrant, player) && existing.MapId != player.MapId))
            {
                existing.Shown = null;
                return RegisterResult.AlreadyRegistered;
            }
            if (existing is null && party is not null && party.LeaderSessionId != player.SessionId)
                return RegisterResult.NotLeader;
            if (existing is not null) _entries.Remove(existing);
            _entries.Add(new Entry(player, player.MapId));
            return RegisterResult.Registered;
        }
    }

 /// <summary>
 /// 1661: removes the registration of <paramref name="player"/> or of the party it is in, returning it (null
 /// when there was none).
 /// </summary>
    public Entry? Cancel(PlayerState player, PartyService parties)
    {
        var partyId = parties.GetPlayerParty(player.SessionId)?.PartyId;
        lock (_gate)
        {
            var entry = FindLocked(player, partyId, parties);
            if (entry is not null) _entries.Remove(entry);
            return entry;
        }
    }

    private Entry? FindLocked(PlayerState player, uint? partyId, PartyService parties)
    {
        foreach (var e in _entries)
        {
            if (ReferenceEquals(e.Registrant, player)) return e;
            if (partyId is { } id && parties.GetPlayerParty(e.SessionId)?.PartyId == id) return e;
        }
        return null;
    }

 /// <summary>
 /// One tick: drops the registrations that ended, and returns the 2972s to send, one per online member of
 /// each waiting party whose class vector changed since it was last sent.
 /// </summary>
    public List<Send> Tick(PlayerStateManager players, PartyService parties, Rules rules)
    {
        var sends = new List<Send>();
        lock (_gate)
        {
            var seenParties = new HashSet<uint>();
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                var party = parties.GetPlayerParty(e.SessionId);
                bool gone = !ReferenceEquals(players.GetPlayer(e.SessionId), e.Registrant)
                    || e.Registrant.MapId != e.MapId
                    
                    || (party is not null && !seenParties.Add(party.PartyId));
                if (gone)
                {
                    _entries.RemoveAt(i--);
                    continue;
                }

                List<PlayerState> online;
                int size;
                if (party is null)
                {
                    online = [e.Registrant];
                    size = 1;
                }
                else
                {
                    var memberIds = party.MemberSessionIds.ToArray();
                    size = memberIds.Length;
                    online = [];
                    foreach (var id in memberIds)
                        if (parties.GetOnlineMember(party, id) is { } member)
                            online.Add(member);
                }

                
                if (size >= rules.PartyNeedCount || online.Count == 0) continue;

                var classes = online
                    .OrderBy(m => PartyAnswerPackets.MemberGid(m))
                    .Select(m => ClassOf(m))
                    .ToArray();
                if (e.Shown is not null && e.Shown.AsSpan().SequenceEqual(classes)) continue;

                e.Shown = classes;
                var body = BuildPartyState2972(rules.MapId, classes);
                foreach (var member in online)
                    sends.Add(new Send(member.SessionId, body));
            }
        }
        return sends;
    }

 /// <summary>
 /// The member's class byte, the same one its party roster record carries at rec+16 (PartyService builds it
 /// from WorldEntryService.CombatJobByte, or PlayerState.Job without a Character snapshot).
 /// </summary>
    public static byte ClassOf(PlayerState member)
        => member.Character is { } c ? WorldEntryService.CombatJobByte(c) : member.Job;

 /// <summary>
 /// Builds NfyTRoomPartyState (2972): <c>[u32 mapId][CompactLen n][n × u8 class]</c>, without envelope.
 /// </summary>
    public static byte[] BuildPartyState2972(uint mapId, ReadOnlySpan<byte> classes)
    {
        using var w = new PacketWriter();
        w.WriteUInt32(mapId);
        w.WriteCompactLength(classes.Length);
        w.WriteBytes(classes);
        return w.ToArray();
    }

 /// <summary>Time from <paramref name="nowUtc"/> to the next multiple of <see cref="TickPeriod"/>, in (0, 5] s.</summary>
    public static TimeSpan UntilNextTick(DateTime nowUtc)
        => TimeSpan.FromTicks(TickPeriod.Ticks - nowUtc.Ticks % TickPeriod.Ticks);

 /// <summary>
 /// Starts the tick loop unless it is already running, with <paramref name="context"/> as what it works with.
 /// Called by every 1660 that reaches registration; the loop stops once nothing is registered.
 /// </summary>
    public void EnsureTicking(TickContext context)
    {
        Volatile.Write(ref _context, context);
        if (Interlocked.CompareExchange(ref _tickAlive, 1, 0) != 0) return;
        _ = Task.Run(RunTicksAsync);
    }

    private async Task RunTicksAsync()
    {
        while (true)
        {
            await Task.Delay(UntilNextTick(DateTime.UtcNow));
            if (Volatile.Read(ref _context) is { } ctx)
            {
                try
                {
                    foreach (var send in Tick(ctx.Players, ctx.Parties, ctx.Rules))
                    {
                        if (ctx.SessionOf(send.SessionId) is { } session)
                            await session.SendInternalRmiAsync(ZoneS2COpcodes.NfyTRoomPartyState, send.Body);
                    }
                }
                catch (Exception ex)
                {
                    ctx.Logger.LogWarning(ex, "Training Room tick failed; retrying on the next one");
                }
            }
            if (Count > 0) continue;
            
            
            Volatile.Write(ref _tickAlive, 0);
            if (Count == 0 || Interlocked.CompareExchange(ref _tickAlive, 1, 0) != 0) return;
        }
    }
}
