using System.Buffers.Binary;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Parties.Application;
using RO2.Parties.Domain;
using RO2.Server.Zone.Models;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Services;

public class PartyState : IPartyView
{
    public uint PartyId { get; set; }
    public uint LeaderSessionId { get; set; }
    public List<uint> MemberSessionIds { get; set; } = [];

 /// <summary>Characters keyed by member session ID, or by a detached key while the member is switching characters.</summary>
    public ConcurrentDictionary<uint, PlayerState> Members { get; } = new();

 /// <summary>Each member's 1-based frame slot, used by the raid view to group members.</summary>
    public ConcurrentDictionary<uint, byte> FrameSlots { get; } = new();

 /// <summary>Each member's Helper flag, keyed by character GID so it survives session changes.</summary>
    public ConcurrentDictionary<ulong, byte> HelperFlags { get; } = new();

 /// <summary>Character GIDs whose Helper flag was revoked for no longer meeting the Helper rule.</summary>
    public ConcurrentDictionary<ulong, byte> LapsedHelperGids { get; } = new();

 /// <summary>When each locked member went away, keyed by character GID so it follows the member across a key change.</summary>
    public ConcurrentDictionary<ulong, DateTime> LockedSince { get; } = new();

    /// <summary>Held while a party decision reads the party into the aggregate and writes the outcome back (no await inside).</summary>
    internal readonly object Gate = new();

    /// <summary>Loot mode a new party starts with (roster header payload+10, set later by 2363).</summary>
    public const byte DefaultItemRule = 2;

    /// <summary>Loot quality a new party starts with (roster header payload+28, set later by 2363).</summary>
    public const byte DefaultItemQuality = 2;

    /// <summary>Loot mode (retail values 2, 3 or 7) and loot quality (retail values 0, 2 or 4).</summary>
    public byte ItemRule { get; set; } = DefaultItemRule;
    public byte ItemQuality { get; set; } = DefaultItemQuality;

    /// <summary>EXP rule set by 1386: 0 equal share, 1 by contribution.</summary>
    public byte ExpRule { get; set; }

    /// <summary>Party type: 0 normal party, 2 raid (see <c>PartyService.PartyTypeRaid</c>).</summary>
    public byte PartyType { get; set; }

 /// <summary>GID of the party's founder, the leader the party was created with.</summary>
    public ulong FounderGid { get; set; }

 /// <summary>Secondary leader slots: each holds a character GID, or 0 for a free slot.</summary>
    public ulong[] SubMasterGids { get; } = new ulong[PartyService.MaxSubMasters];

    public string Notice { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public uint LeaderKey => LeaderSessionId;
    public IReadOnlyList<uint> MemberKeys => MemberSessionIds.ToArray();
    public byte FrameSlot(uint memberKey) => FrameSlots.GetValueOrDefault(memberKey);
    public bool IsLeader(uint memberKey) => LeaderSessionId == memberKey;
}

/// <summary>Why a member left the party. Each reason is announced to the remaining members differently.</summary>
public enum PartyLeaveReason
{
 /// <summary>The member left by its own request, or stayed locked until the lock expired.</summary>
    Left,
 /// <summary>The leader removed the member.</summary>
    Kicked,
}

/// <summary>Result of a helper declaration.</summary>
public enum HelperDeclareResult
{
 /// <summary>The flag was stored, the answer was sent and the roster was pushed.</summary>
    Done,
 /// <summary>The character is in no party on this session; nothing is sent.</summary>
    NotInParty,
 /// <summary>The declaration does not meet the Helper rule; nothing is sent.</summary>
    NotEligible,
}

/// <summary>
/// Manages party state and operations. It keeps the live registry (<see cref="PartyState"/>, the session and
/// detached-key map), the lock-expiry timer and the wire builders; the decisions are made by
/// <see cref="RO2.Parties.Domain.PartyAggregate"/> (see <c>PartyService.Decisions.cs</c>).
/// </summary>
public partial class PartyService
{
    private readonly ConcurrentDictionary<uint, PartyState> _parties = new();
    private readonly ConcurrentDictionary<uint, uint> _playerToParty = new();
    private uint _nextPartyId = 1;
    /// <summary>Serializes party creation: the inviter has no party Gate yet when the first accept creates it.</summary>
    private readonly object _partyCreationGate = new();

 /// <summary>Highest detached member key. Detached keys count down from here, so they do not clash with session IDs.</summary>
    private const uint DetachedKeyCeiling = 0x00FF_FFFF;
    private uint _nextDetachedKey = DetachedKeyCeiling + 1;
    private readonly PlayerStateManager _playerManager;
    private readonly BroadcastService _broadcast;
    private readonly ILogger<PartyService> _logger;
    private readonly IGuildNameLookup _guildNames;
    private readonly RO2.Parties.Application.IClock _clock;

 /// <summary>Open invitations, keyed by the target's session. A target can have at most one open invitation.</summary>
    private readonly ConcurrentDictionary<uint, PendingInvite> _pendingInvites = new();

 /// <summary>An open party invitation: the inviter's session beside the invitation the aggregate decides on.</summary>
    private sealed record PendingInvite(uint InviterSessionId, PartyInvite Invite);

    /// <summary>2902 AnsHelperDeclare result 0: the declaration was accepted.</summary>
    private const ushort HelperDeclareResultOk = 0;

 /// <summary>Maximum number of members: 20 for a raid, 5 otherwise.</summary>
    public static int MaxMembers(PartyState party)
        => party.PartyType == PartyAggregate.PartyTypeRaid ? PartyAggregate.RaidMaxMembers : PartyAggregate.NormalMaxMembers;

 /// <summary>How long a locked member stays in its party before it is removed (595 s; with the 5 s sweep, dropped 595 to 600 s after the lock began).</summary>
    public static readonly TimeSpan LockedMemberLifetime = PartyAggregate.LockedMemberLifetime;

 /// <summary>Seconds between expiry sweeps; removals land on this grid.</summary>
    private const int LockedMemberSweepSeconds = 5;

 /// <summary>How often expired locked members are checked.</summary>
    public static readonly TimeSpan LockedMemberSweepPeriod = TimeSpan.FromSeconds(LockedMemberSweepSeconds);

    private int _lockSweepAlive;

    public PartyService(PlayerStateManager playerManager, BroadcastService broadcast, ILogger<PartyService> logger,
        IGuildNameLookup? guildNames = null, RO2.Parties.Application.IClock? clock = null)
    {
        _playerManager = playerManager;
        _broadcast = broadcast;
        _logger = logger;
        _guildNames = guildNames ?? new GuildNameLookup(new GuildDirectory());
        _clock = clock ?? new PartyClock(new SystemClock());

        PcRecordParty.Register(this, p =>
            ReferenceEquals(_playerManager.GetPlayer(p.SessionId), p) && GetPlayerParty(p.SessionId) is { } party
                ? PartyAnswerPackets.PartyHandle(party)
                : null);
    }

    /// <summary>The party with that ID, or null when it no longer exists.</summary>
    public PartyState? GetParty(uint partyId) => _parties.GetValueOrDefault(partyId);

    public PartyState? GetPlayerParty(uint sessionId)
    {
        if (_playerToParty.TryGetValue(sessionId, out var partyId))
            return _parties.GetValueOrDefault(partyId);
        return null;
    }

 /// <summary>The read-only view of the session's party, or null when it is in none.</summary>
    public IPartyView? GetPartyView(uint sessionId) => GetPlayerParty(sessionId);

 /// <summary>
 /// Handles a 1385 loot-rule change: stores the rule and quality, answers the requester (1385 ack), then tells
 /// every member (2363).
 /// </summary>
    public async Task SetItemRuleAsync(PartyState party, ProudSession requester, uint requesterHandle, byte lootMode, byte quality)
    {
        party.ItemRule = lootMode;
        party.ItemQuality = quality;

        await requester.SendInternalRmiAsync(ZoneS2COpcodes.OnPartyChangeItem,
            PartyAnswerPackets.BuildResultAck(PartyStatePackets.MsgTagOnPartyChangeItem, requesterHandle));

        await BroadcastBodyToPartyAsync(party, ZoneS2COpcodes.OnPartyNotifyChangeItem,
            PartyAnswerPackets.BuildPartyNotifyChangeItem2363(PartyAnswerPackets.PartyHandle(party), lootMode, quality));
    }

 /// <summary>
 /// Notifies the other members that a member's zone session has ended. The member is locked in place (2360, kind 1).
 /// </summary>
    public Task NotifyMemberOfflineAsync(PlayerState departed) => MemberWentOfflineAsync(departed, detachSession: false);

 /// <summary>
 /// Handles a 1418 character switch: the character leaves for character select while the connection stays open.
 /// </summary>
    public Task NotifyMemberSwitchedCharacterAsync(PlayerState departed) => MemberWentOfflineAsync(departed, detachSession: true);

 /// <summary>
 /// Moves the member on a session onto a new detached key, keeping its roster position, frame slot and leader role.
 /// </summary>
    private uint DetachFromSession(PartyState party, uint sessionId)
    {
        var index = party.MemberSessionIds.IndexOf(sessionId);
        if (index < 0)
        {
            _playerToParty.TryRemove(sessionId, out _);
            return sessionId;
        }

        var key = Interlocked.Decrement(ref _nextDetachedKey);
        _playerToParty[key] = party.PartyId;
        party.MemberSessionIds[index] = key;
        if (party.Members.TryRemove(sessionId, out var member)) party.Members[key] = member;
        if (party.FrameSlots.TryRemove(sessionId, out var slot)) party.FrameSlots[key] = slot;
        if (party.LeaderSessionId == sessionId) party.LeaderSessionId = key;
        _playerToParty.TryRemove(sessionId, out _);
        return key;
    }

 /// <summary>
 /// Rebinds a player that has loaded a map to its party member record, when its character is still held by a party.
 /// </summary>
    public async Task<bool> RebindReturningMemberAsync(PlayerState player)
    {
        if (_playerToParty.ContainsKey(player.SessionId)) return false;
        var gid = MemberGid(player);
        if (gid == 0) return false;

        foreach (var party in _parties.Values)
        {
            uint staleKey;
            lock (party.Gate)
            {
                staleKey = FindOfflineMemberByGid(party, gid);
                if (staleKey == 0) continue;
                ReplaceMemberSession(party, staleKey, player);
            }



            await SendInternalToPartyExceptAsync(party, player.SessionId, ZoneS2COpcodes.OnPartyNotifyJoin,
                BuildJoinDeltaBody(party, player));
            var session = _broadcast.GetSession(player.SessionId);
            if (session != null)
                await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPartyNotifyCreate,
                    BuildRosterBody(party, create: true));

            _logger.LogInformation("{Name} is back in the world; bound again to party {PartyId}",
                player.Name, party.PartyId);
            return true;
        }
        return false;
    }

 /// <summary>
 /// Handles a 1384 from a session that is in no party: removes the character from every party that still holds it as
 /// an offline member.
 /// </summary>
    public async Task<int> LeaveAsOfflineMemberAsync(PlayerState player)
    {
        var gid = MemberGid(player);
        if (gid == 0) return 0;

        var left = 0;
        foreach (var party in _parties.Values)
        {
            var key = FindOfflineMemberByGid(party, gid);
            if (key == 0) continue;
            await LeavePartyAsync(key, PartyLeaveReason.Left);
            left++;
        }
        return left;
    }

 /// <summary>Starts the lock expiry loop unless it is already running.</summary>
    private void EnsureLockedMemberSweep()
    {
        if (Interlocked.CompareExchange(ref _lockSweepAlive, 1, 0) != 0) return;
        _ = Task.Run(RunLockedMemberSweepAsync);
    }

    private async Task RunLockedMemberSweepAsync()
    {
        using var timer = new PeriodicTimer(LockedMemberSweepPeriod);
        while (await timer.WaitForNextTickAsync())
        {
            try
            {
                await ExpireLockedMembersAsync(DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Party lock expiry failed; retrying next period");
            }
            if (AnyLockedMember()) continue;


            Volatile.Write(ref _lockSweepAlive, 0);
            if (!AnyLockedMember() || Interlocked.CompareExchange(ref _lockSweepAlive, 1, 0) != 0) return;
        }
    }

    private bool AnyLockedMember()
    {
        foreach (var party in _parties.Values)
            if (!party.LockedSince.IsEmpty) return true;
        return false;
    }

 /// <summary>
 /// After a member gains a level, removes the Helper flag from party members who no longer meet the Helper rule.
 /// </summary>
    public async Task RevokeLapsedHelpersAsync(PlayerState leveler)
    {
        var party = GetPlayerParty(leveler.SessionId);
        if (party == null || (party.HelperFlags.IsEmpty && party.LapsedHelperGids.IsEmpty)
            || GetOnlineMember(party, leveler.SessionId) == null)
            return;


        var lapsedBefore = party.LapsedHelperGids.Keys.ToArray();

        var revoked = new List<(ulong Gid, uint SessionId)>();
        lock (party.Gate)
            foreach (var (gid, _) in party.HelperFlags.ToArray())
            {
                var helperId = FindMemberByGid(party, gid);
                var helper = helperId != 0 ? KnownMember(party, helperId) : null;

                if (helper == null || MeetsHelperRule(party, helper)) continue;

                if (party.HelperFlags.TryRemove(gid, out _))
                {
                    revoked.Add((gid, helperId));
                    party.LapsedHelperGids[gid] = 0;
                }
            }
        if (revoked.Count > 0)
        {
            foreach (var (gid, helperId) in revoked)
            {
                await SendInternalToPartyExceptAsync(party, helperId, ZoneS2COpcodes.NfyHelperNotice,
                    HelperPackets.BuildHelperNotice2903(gid, HelperPackets.FlagQuit));
                _logger.LogInformation("Party {PartyId}: Helper flag of GID 0x{Gid:X16} taken away after {Name} reached level {Level}",
                    party.PartyId, gid, leveler.Name, leveler.Level);
            }
            await BroadcastRosterAsync(party);
        }


        if (lapsedBefore.Length == 0) return;
        var levelerSession = _broadcast.GetSession(leveler.SessionId);
        if (levelerSession == null) return;
        foreach (var gid in lapsedBefore)
        {

            if (!party.LapsedHelperGids.ContainsKey(gid)) continue;
            var helperId = FindMemberByGid(party, gid);
            if (helperId == 0 || helperId == leveler.SessionId) continue;

            if (KnownMember(party, helperId) is not { } helper) continue;
            if (!LapsedHelperStillAbove(helper.Level, leveler.Level)) continue;
            await levelerSession.SendInternalRmiAsync(ZoneS2COpcodes.NfyHelperNotice,
                HelperPackets.BuildHelperNotice2903(gid, HelperPackets.FlagQuit));
        }
    }

 /// <summary>Whether a lapsed helper at <paramref name="helperLevel"/> still stands above <paramref name="memberLevel"/>.</summary>
    internal static bool LapsedHelperStillAbove(byte helperLevel, byte memberLevel)
        => helperLevel > memberLevel && helperLevel < memberLevel + HelperPackets.LevelGap;

 /// <summary>Whether <paramref name="candidate"/> meets the client's Helper rule in the party.</summary>
    private bool MeetsHelperRule(PartyState party, PlayerState candidate)
    {
        var candidateGid = MemberGid(candidate);
        var otherLevels = new List<byte>(party.MemberSessionIds.Count);
        foreach (var memberId in party.MemberSessionIds.ToArray())
        {
            var other = KnownMember(party, memberId);
            if (other == null || MemberGid(other) == candidateGid) continue;
            otherLevels.Add(other.Level);
        }
        return HelperPackets.CanBeHelper(candidate.Level, otherLevels);
    }

 /// <summary>Registers a party built outside JoinPartyAsync and binds its member keys to it. Used by tests.</summary>
    internal void Track(PartyState party)
    {
        _parties[party.PartyId] = party;
        foreach (var memberId in party.MemberSessionIds)
            _playerToParty[memberId] = party.PartyId;
    }

 /// <summary>
 /// Handles a 1386 request: the leader sets the party's EXP rule, which loot applies to every kill reward.
 /// </summary>
    public async Task<bool> ChangeExpRuleAsync(uint sessionId, uint requesterHandle, byte newRule)
    {
        var party = GetPlayerParty(sessionId);
        if (party == null || party.LeaderSessionId != sessionId) return false;

        party.ExpRule = newRule;

        var session = _broadcast.GetSession(sessionId);
        if (session != null)
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPartyChangeExp,
                PartyStatePackets.BuildOnPartyChangeExpAck(requesterHandle));
        _logger.LogInformation("Party {PartyId}: exp rule changed to {Rule}", party.PartyId, newRule);
        return true;
    }

 /// <summary>Handles a 1389 notice update. The leader's notice is stored on the party.</summary>
    public bool SetNotice(uint sessionId, string notice)
    {
        var party = GetPlayerParty(sessionId);
        if (party == null || party.LeaderSessionId != sessionId) return false;

        party.Notice = notice;
        _logger.LogInformation("Party {PartyId}: notice set ({Length} chars); 2368 is not sent, the client does nothing with it",
            party.PartyId, notice.Length);
        return true;
    }

 /// <summary>Handles a 1391 group change for two members, given as GID and group pairs.</summary>
    public bool ChangeGroup(uint sessionId, ulong gidA, byte groupA, ulong gidB, byte groupB)
    {
        var party = GetPlayerParty(sessionId);
        if (party == null || party.LeaderSessionId != sessionId) return false;
        if (gidA == 0 && gidB == 0) return false;

        var memberA = FindMemberByGid(party, gidA);
        var memberB = FindMemberByGid(party, gidB);
        if ((gidA != 0 && memberA == 0) || (gidB != 0 && memberB == 0))
        {
            _logger.LogDebug("Party {PartyId}: 1391 names a GID that is no member ({GidA:X16}, {GidB:X16}); dropped",
                party.PartyId, gidA, gidB);
            return false;
        }

        _logger.LogInformation("Party {PartyId}: 1391 group change {GidA:X16} -> {GroupA}, {GidB:X16} -> {GroupB}; no group state is kept and 2355/2370 are not sent",
            party.PartyId, gidA, groupA, gidB, groupB);
        return true;
    }

 /// <summary>Handles a 1390 right change. Only the leader's request is accepted.</summary>
    public bool SetRight(uint sessionId, uint subject, byte grant)
    {
        var party = GetPlayerParty(sessionId);
        if (party == null || party.LeaderSessionId != sessionId) return false;

        _logger.LogInformation("Party {PartyId}: 1390 right {Action} for {Subject:X8} from the leader; logged only (2354 changes nothing, 2369 crashes the client)",
            party.PartyId, grant != 0 ? "grant" : "revoke", subject);
        return true;
    }

 /// <summary>Returns the secondary leader slots of the party, each a character GID or 0 for a free slot.</summary>
    public ulong[] SubMastersInRoster(PartyState party)
    {
        ulong[] slots;
        lock (party.SubMasterGids) slots = (ulong[])party.SubMasterGids.Clone();
        for (var i = 0; i < slots.Length; i++)
            if (slots[i] != 0 && FindMemberByGid(party, slots[i]) == 0) slots[i] = 0;
        return slots;
    }

 /// <summary>Returns the member's live state while its character is in the world, or null otherwise.</summary>
    public PlayerState? GetOnlineMember(PartyState party, uint sessionId)
    {
        var live = _playerManager.GetPlayer(sessionId);
        if (live == null) return null;
        return party.Members.TryGetValue(sessionId, out var joined) && MemberGid(joined) != MemberGid(live)
            ? null
            : live;
    }

 /// <summary>Returns the member's character: live while online, otherwise its last known state.</summary>
    private PlayerState? KnownMember(PartyState party, uint sessionId)
        => GetOnlineMember(party, sessionId) ?? party.Members.GetValueOrDefault(sessionId);

 /// <summary>Returns the session of the member whose character has the given GID, online or not, or 0 when none.</summary>
    public uint FindMemberByGid(PartyState party, ulong gid)
    {
        if (gid == 0) return 0;
        foreach (var memberId in party.MemberSessionIds.ToArray())
            if (MemberGid(KnownMember(party, memberId)) == gid)
                return memberId;
        return 0;
    }

    private uint FindOfflineMemberByGid(PartyState party, ulong gid)
    {
        var memberId = FindMemberByGid(party, gid);
        return memberId != 0 && GetOnlineMember(party, memberId) == null ? memberId : 0;
    }

 /// <summary>Returns the member's 1-based frame slot, or 0 when it holds none.</summary>
    public static byte GetFrameSlot(PartyState party, uint sessionId)
        => party.FrameSlots.GetValueOrDefault(sessionId);

 /// <summary>Adds a new member at the end of the roster, in the lowest free frame slot.</summary>
    private void AddMember(PartyState party, PlayerState member)
    {
        party.FrameSlots[member.SessionId] = LowestFreeSlot(party);
        party.Members[member.SessionId] = member;
        party.MemberSessionIds.Add(member.SessionId);
        _playerToParty[member.SessionId] = party.PartyId;
    }

    private static byte LowestFreeSlot(PartyState party)
    {
        var used = new HashSet<byte>(party.FrameSlots.Values);
        for (var slot = 1; slot <= byte.MaxValue; slot++)
            if (!used.Contains((byte)slot))
                return (byte)slot;
        return 0;
    }

 /// <summary>
 /// Replaces an offline member's session with the rejoining session, keeping its place in the roster, frame slot and role.
 /// </summary>
    private void ReplaceMemberSession(PartyState party, uint staleSessionId, PlayerState joiner)
    {

        if (MemberGid(joiner) is var returnGid and not 0)
            party.LockedSince.TryRemove(returnGid, out _);
        var index = party.MemberSessionIds.IndexOf(staleSessionId);
        if (index < 0)
        {
            AddMember(party, joiner);
            return;
        }
        party.MemberSessionIds[index] = joiner.SessionId;
        party.Members.TryRemove(staleSessionId, out _);
        party.Members[joiner.SessionId] = joiner;
        party.FrameSlots[joiner.SessionId] = party.FrameSlots.TryRemove(staleSessionId, out var slot)
            ? slot
            : LowestFreeSlot(party);
        if (party.LeaderSessionId == staleSessionId) party.LeaderSessionId = joiner.SessionId;

        _playerToParty.TryRemove(staleSessionId, out _);
        _playerToParty[joiner.SessionId] = party.PartyId;
    }

 /// <summary>Broadcasts a game server-to-client message to every current party member.</summary>
    public async Task BroadcastToPartyAsync(PartyState party, ushort opcode, Action<RO2.Network.Packets.PacketWriter> writeAction)
    {
        var payload = BuildPayload(writeAction);
        await SendInternalToPartyAsync(party, opcode, payload);
    }

 /// <summary>Broadcasts a pre-built server-to-client body to every current party member.</summary>
    public async Task BroadcastBodyToPartyAsync(PartyState party, ushort opcode, ReadOnlyMemory<byte> body)
    {
        await SendInternalToPartyAsync(party, opcode, body);
    }







    private async Task SendInternalToPartyAsync(PartyState party, ushort opcode, ReadOnlyMemory<byte> payload)
    {
        foreach (var memberId in party.MemberSessionIds.ToArray())
        {
            if (GetOnlineMember(party, memberId) == null) continue;
            var session = _broadcast.GetSession(memberId);
            if (session != null)
                await session.SendInternalRmiAsync(opcode, payload);
        }
    }

    private async Task SendInternalToPartyExceptAsync(PartyState party, uint exceptSessionId, ushort opcode,
        ReadOnlyMemory<byte> payload)
    {
        foreach (var memberId in party.MemberSessionIds.ToArray())
        {
            if (memberId == exceptSessionId) continue;
            if (GetOnlineMember(party, memberId) == null) continue;
            var session = _broadcast.GetSession(memberId);
            if (session != null)
                await session.SendInternalRmiAsync(opcode, payload);
        }
    }

    private static byte[] BuildPayload(Action<RO2.Network.Packets.PacketWriter> writeAction)
    {
        using var pw = new RO2.Network.Packets.PacketWriter();
        writeAction(pw);
        return pw.GetWrittenSpan().ToArray();
    }
















 /// <summary>Returns the party handle the client expects for the party.</summary>
    private static uint SynthPartyHandle(PartyState party) => PartyAnswerPackets.PartyHandle(party);

 /// <summary>Returns the actor handle of a PC session.</summary>
    private static uint SynthPcHandle(uint sessionId) => PlayerStateManager.BuildActorHandle(sessionId);

    private static ulong MemberGid(PlayerState? p) => PartyAnswerPackets.MemberGid(p);

 /// <summary>What a member's record shows of <paramref name="p"/>; the guild name comes through the guild port.</summary>
    private MemberView ToView(PlayerState p, bool online)
    {
        var c = p.Character;
        var (proJobLevel, proJobId) = c != null ? CharacterSerializer.WireProJob(c) : ((byte)0, (byte)0);
        return new MemberView(MemberGid(p), p.Name, p.Level, c != null ? CharacterHeadRules.CombatJobByte(c) : p.Job,
            (uint)Math.Max(0, p.Hp), (uint)Math.Max(0, p.MaxHp), (uint)Math.Max(0, p.Mp), (uint)Math.Max(0, p.MaxMp),
            p.MapId, p.X, p.Y, p.Z,
            c != null ? CharacterHeadRules.CreationProfessionByte(c) : (byte)0, proJobLevel, proJobId,
            _guildNames.GuildNameOf(p.CharacterId), online);
    }

 /// <summary>Builds one member's 208-byte record with identity and stat fields. Shared by the roster broadcasts.</summary>
    private static byte[] BuildMemberRecordFor(uint memberSessionId, MemberView? v, byte frameSlot, byte helperFlag)
    {
        if (v == null)
            return PartyNotifyPackets.BuildMemberRecord(SynthPcHandle(memberSessionId), 0UL, string.Empty);

        var record = PartyStatePackets.BuildPartyMemberRecord(
            SynthPcHandle(memberSessionId), v.Gid, v.Name,
            v.Level, v.Job,
            v.Hp, v.MaxHp, v.Mp, v.MaxMp,
            v.MapId, v.X, v.Y, v.Z,
            frameSlot);
        PartyNotifyPackets.ApplyMemberState(record, v.Online, v.Profession, v.ProJobLevel, v.ProJobId, v.GuildName);
        record[HelperPackets.RecHelperFlagOff] = helperFlag;
        return record;
    }

 /// <summary>Returns the member's Helper flag: 0 unless it has declared itself a Helper in this party.</summary>
    private static byte HelperFlagOf(PartyState party, PlayerState? p)
        => MemberGid(p) is var gid and not 0 ? party.HelperFlags.GetValueOrDefault(gid) : (byte)0;

 /// <summary>Builds the member records for the roster broadcasts.</summary>
    private List<byte[]> BuildRosterRecords(PartyState party)
    {
        var keys = party.MemberSessionIds.ToArray(); // a join can add a member while this runs outside the Gate
        var records = new List<byte[]>(keys.Length);
        foreach (var memberId in keys)
        {
            var known = KnownMember(party, memberId);
            records.Add(BuildMemberRecordFor(memberId,
                known == null ? null : ToView(known, online: GetOnlineMember(party, memberId) != null),
                GetFrameSlot(party, memberId), HelperFlagOf(party, known)));
        }
        return records;
    }

 /// <summary>Returns the member GIDs in roster order, matching the order of the roster records.</summary>
    public List<ulong> BuildMemberGidList(PartyState party)
    {
        var keys = party.MemberSessionIds.ToArray();
        var gids = new List<ulong>(keys.Length);
        foreach (var memberId in keys)
            gids.Add(MemberGid(KnownMember(party, memberId)));
        return gids;
    }

 /// <summary>Builds a zero-filled roster header of the required length, with the current leader's GID filled in.</summary>
    private byte[] BuildRosterHeader(PartyState party, int headerLen)
    {
        var hdr = new byte[headerLen];
        var leaderGid = MemberGid(KnownMember(party, party.LeaderSessionId));
        BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(PartyNotifyPackets.HdrLeaderGidOff), leaderGid);


        BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(PartyNotifyPackets.HdrLeaderGid2Off),
            party.FounderGid != 0 ? party.FounderGid : leaderGid);
        hdr[RosterHdrPartyTypeOff] = party.PartyType;
        hdr[RosterHdrLootModeOff] = party.ItemRule;
        hdr[RosterHdrLootQualityOff] = party.ItemQuality;
        var subMasters = SubMastersInRoster(party);
        for (var i = 0; i < subMasters.Length; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(RosterHdrSubMasterOff + RosterHdrGidSize * i), subMasters[i]);
        return hdr;
    }

 /// <summary>Offset of the first secondary leader slot in the roster header (payload+30; three u64 slots, the founder GID follows at payload+54).</summary>
    private const int RosterHdrSubMasterOff = 30;

 /// <summary>Size of one GID slot (u64) in the roster header.</summary>
    private const int RosterHdrGidSize = sizeof(ulong);

 /// <summary>Offset of the party type in the roster header (payload+8): 0 normal, 2 raid.</summary>
    private const int RosterHdrPartyTypeOff = 8;

 /// <summary>Offset of the loot mode in the roster header (payload+10). The loot quality is at payload+28.</summary>
    private const int RosterHdrLootModeOff = 10;
    private const int RosterHdrLootQualityOff = 28;

 /// <summary>
 /// Assembles the roster body: 2357 (OnPartyNotifyCreate) when the party has just formed, otherwise 2361 (OnPartyNotifyUpdate).
 /// </summary>
    public byte[] BuildRosterBody(PartyState party, bool create = false)
    {
        var handle = SynthPartyHandle(party);
        var records = BuildRosterRecords(party);
        return create
            ? PartyNotifyPackets.BuildPartyNotifyCreate2357(
                handle, BuildRosterHeader(party, PartyNotifyPackets.CreateHeaderLen), records)
            : PartyNotifyPackets.BuildPartyNotifyUpdate2361(
                handle, BuildRosterHeader(party, PartyNotifyPackets.UpdateHeaderLen), records);
    }

 /// <summary>Assembles the 2359 (OnPartyNotifyJoin) delta for a single joining member.</summary>
    public byte[] BuildJoinDeltaBody(PartyState party, PlayerState joiner)
    {


        var record = BuildMemberRecordFor(joiner.SessionId, ToView(joiner, online: true),
            GetFrameSlot(party, joiner.SessionId), HelperFlagOf(party, joiner));
        return PartyNotifyPackets.BuildPartyNotifyJoin2359(
            SynthPartyHandle(party), new byte[PartyNotifyPackets.JoinHeaderLen], record);
    }

 /// <summary>Sends the full party roster to every current member. Called on every roster-changing event.</summary>
    public async Task BroadcastRosterAsync(PartyState party, bool create = false)
    {
        if (party.MemberSessionIds.Count == 0) return;
        var body = BuildRosterBody(party, create);
        var opcode = create ? ZoneS2COpcodes.OnPartyNotifyCreate : ZoneS2COpcodes.OnPartyNotifyUpdate;
        await SendInternalToPartyAsync(party, opcode, body);
    }
}
