using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Parties.Application;
using RO2.Parties.Domain;
using RO2.Server.Zone.Models;
using DomainLeaveReason = RO2.Parties.Domain.PartyLeaveReason;

namespace RO2.Server.Zone.Services;

/// <summary>
/// The party operations that are decisions. Each one snapshots the party into a <see cref="PartyAggregate"/>, lets the
/// aggregate decide under the party's gate (no await inside), writes the outcome back to the <see cref="PartyState"/>
/// the rest of the host reads, and then delivers the decision's effects in order through the wire builders.
/// </summary>
public partial class PartyService
{
    /// <summary>One party as the aggregate sees it, with the host keys the aggregate does not know.</summary>
    private sealed class PartySnapshot
    {
        public required PartyAggregate Aggregate { get; init; }
        public required PartyMemberDirectory Directory { get; init; }
        public required IReadOnlySet<ulong> Online { get; init; }
        public required ulong LeaderGidAtStart { get; init; }
        public Dictionary<ulong, uint> KeyOf => Directory.KeyOf;
        public Dictionary<uint, ulong> GidOfKey => Directory.GidOfKey;
    }

    /// <summary>Host adapter of <see cref="IMemberDirectory"/> over one party's member keys and the live players.</summary>
    private sealed class PartyMemberDirectory : IMemberDirectory
    {
        private readonly PartyService _service;
        private readonly Dictionary<ulong, PlayerState> _known = [];
        private readonly HashSet<ulong> _online = [];

        /// <summary>Every member's GID in roster order, 0 for a member whose character is unknown.</summary>
        public ulong[] Gids { get; }
        public Dictionary<ulong, uint> KeyOf { get; } = [];
        public Dictionary<uint, ulong> GidOfKey { get; } = [];

        public PartyMemberDirectory(PartyService service, PartyState party)
        {
            _service = service;
            var keys = party.MemberSessionIds.ToArray();
            Gids = new ulong[keys.Length];
            for (var i = 0; i < keys.Length; i++)
            {
                var known = service.KnownMember(party, keys[i]);
                var gid = MemberGid(known);
                Gids[i] = gid;
                if (gid == 0) continue;
                GidOfKey[keys[i]] = gid;
                if (!KeyOf.TryAdd(gid, keys[i])) continue;
                _known[gid] = known!;
                if (service.GetOnlineMember(party, keys[i]) != null) _online.Add(gid);
            }
        }

        public bool IsOnline(ulong gid) => _online.Contains(gid);

        public MemberView? Snapshot(ulong gid)
            => _known.TryGetValue(gid, out var p) ? _service.ToView(p, _online.Contains(gid)) : null;
    }

    private PartySnapshot Snapshot(PartyState party)
    {
        var directory = new PartyMemberDirectory(this, party);
        var keys = party.MemberSessionIds.ToArray();
        Dictionary<ulong, byte> slots = [];
        for (var i = 0; i < keys.Length; i++)
            if (directory.Gids[i] != 0 && party.FrameSlots.TryGetValue(keys[i], out var slot))
                slots.TryAdd(directory.Gids[i], slot);
        ulong[] subMasters;
        lock (party.SubMasterGids) subMasters = (ulong[])party.SubMasterGids.Clone();
        var leaderGid = directory.GidOfKey.GetValueOrDefault(party.LeaderSessionId);
        var aggregate = PartyAggregate.Restore(party.PartyId, party.FounderGid, party.CreatedAt, leaderGid, party.PartyType,
            directory.Gids, slots, party.HelperFlags, party.LapsedHelperGids.Keys, party.LockedSince, subMasters);
        return new PartySnapshot
        {
            Aggregate = aggregate,
            Directory = directory,
            Online = directory.OnlineAmong(directory.Gids.Where(g => g != 0)),
            LeaderGidAtStart = leaderGid,
        };
    }

    /// <summary>Writes what a decision changed in the aggregate back to the party the host reads.</summary>
    private static void Commit(PartyState party, PartySnapshot snap)
    {
        var aggregate = snap.Aggregate;
        party.PartyType = aggregate.PartyType;
        if (aggregate.LeaderGid != snap.LeaderGidAtStart && snap.KeyOf.TryGetValue(aggregate.LeaderGid, out var leaderKey))
            party.LeaderSessionId = leaderKey;
        lock (party.SubMasterGids)
            for (var i = 0; i < party.SubMasterGids.Length; i++) party.SubMasterGids[i] = aggregate.SubMasters[i];
        SyncTable(party.HelperFlags, aggregate.HelperFlags);
        SyncTable(party.LockedSince, aggregate.LockedSince);
        SyncTable(party.LapsedHelperGids, aggregate.LapsedHelpers.ToDictionary(g => g, _ => (byte)0));
        foreach (var (gid, key) in snap.KeyOf)
            if (aggregate.FrameSlots.TryGetValue(gid, out var slot) && party.MemberSessionIds.Contains(key))
                party.FrameSlots[key] = slot;
    }

    private static void SyncTable<TValue>(System.Collections.Concurrent.ConcurrentDictionary<ulong, TValue> table,
        IReadOnlyDictionary<ulong, TValue> source)
    {
        foreach (var gid in table.Keys)
            if (!source.ContainsKey(gid)) table.TryRemove(gid, out _);
        foreach (var (gid, value) in source) table[gid] = value;
    }

    /// <summary>A member left (or was removed): takes it out of the host's key tables and, when the party ended, out of the registry.</summary>
    private void CommitLeave(PartyState party, PartySnapshot snap, uint leaverKey, PartyOutcome outcome)
    {
        party.MemberSessionIds.Remove(leaverKey);
        party.FrameSlots.TryRemove(leaverKey, out _);
        party.Members.TryRemove(leaverKey, out _);
        if (outcome.Result == PartyResult.Disbanded)
        {
            foreach (var memberId in party.MemberSessionIds) _playerToParty.TryRemove(memberId, out _);
            _parties.TryRemove(party.PartyId, out _);
        }
        Commit(party, snap);
    }

    private Task ApplyAsync(PartyState? party, IReadOnlyList<PartyEffect> effects, IReadOnlyDictionary<ulong, uint> keys,
        uint requesterHandle = 0, PlayerState? joiner = null)
        => PartyEffectApplier.ApplyAsync(effects, new ZonePartyNotifier(this, party, keys, requesterHandle, joiner));

    private static DomainLeaveReason ToDomain(PartyLeaveReason reason)
        => reason == PartyLeaveReason.Kicked ? DomainLeaveReason.Kicked : DomainLeaveReason.Left;

    /// <summary>Delivers a decision's effects: each one becomes the existing wire builder sent over the recipient's session.</summary>
    private sealed class ZonePartyNotifier(PartyService service, PartyState? party, IReadOnlyDictionary<ulong, uint> keys,
        uint requesterHandle, PlayerState? joiner) : IPartyNotifier
    {
        public async Task SendToMemberAsync(ulong gid, PartyMessage message)
        {
            if (!keys.TryGetValue(gid, out var key)) return;
            var session = service._broadcast.GetSession(key);
            if (session == null || Wire(message, key) is not var (opcode, body)) return;
            await session.SendInternalRmiAsync(opcode, body);
        }

        public async Task SendToPartyAsync(PartyMessage message, ulong? exceptGid)
        {
            if (party == null || Wire(message, 0) is not var (opcode, body)) return;
            if (exceptGid is { } except && keys.TryGetValue(except, out var exceptKey))
                await service.SendInternalToPartyExceptAsync(party, exceptKey, opcode, body);
            else
                await service.SendInternalToPartyAsync(party, opcode, body);
        }

        public async Task SendRosterAsync(bool create, ulong? onlyGid)
        {
            if (party == null) return;
            if (onlyGid is not { } gid)
            {
                await service.BroadcastRosterAsync(party, create);
                return;
            }
            var session = keys.TryGetValue(gid, out var key) ? service._broadcast.GetSession(key) : null;
            if (session != null)
                await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPartyNotifyCreate, service.BuildRosterBody(party, create: true));
        }

        public async Task SendJoinDeltaAsync(ulong joinerGid)
        {
            if (party == null || joiner == null) return;
            await service.SendInternalToPartyExceptAsync(party, joiner.SessionId, ZoneS2COpcodes.OnPartyNotifyJoin,
                service.BuildJoinDeltaBody(party, joiner));
        }

        public async Task SendDestroyAsync(int copies, IReadOnlyList<ulong> recipients)
        {
            if (party == null) return;
            var body = PartyStatePackets.BuildOnPartyNotifyDestroy2358(SynthPartyHandle(party));
            foreach (var gid in recipients)
            {
                var session = keys.TryGetValue(gid, out var key) ? service._broadcast.GetSession(key) : null;
                if (session == null) continue;
                for (var copy = 0; copy < copies; copy++)
                    await session.SendInternalRmiAsync(PartyStatePackets.OpcodeOnPartyNotifyDestroy, body);
            }
        }

        public async Task SendLayoffAsync(ulong gid)
        {
            var session = keys.TryGetValue(gid, out var key) ? service._broadcast.GetSession(key) : null;
            if (session != null)
                await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPartyNotifyLayoff, PartyLayoffPackets.BuildPartyNotifyLayoff2367());
        }

        public void ScheduleLockSweep() => service.EnsureLockedMemberSweep();

        private (ushort Opcode, byte[] Body)? Wire(PartyMessage m, uint recipientKey)
        {
            var handle = party == null ? 0u : SynthPartyHandle(party);
            return m.Kind switch
            {
                PartyMessageKind.InviteResult => (ZoneS2COpcodes.OnPartyInvite,
                    PartyStatePackets.BuildOnPartyInvite(SynthPcHandle(recipientKey), (short)m.Code, m.Text, m.Value)),
                PartyMessageKind.InviteDialog => (ZoneS2COpcodes.OnPartyJoin,
                    PartyStatePackets.BuildOnPartyJoin(SynthPcHandle(recipientKey), m.Gid, m.Value, m.Text)),
                PartyMessageKind.Error => (ZoneS2COpcodes.OnPartyError,
                    PartyStatePackets.BuildOnPartyError(requesterHandle, (short)m.Code, m.Text)),
                PartyMessageKind.Out => (PartyNotifyOutPackets.Opcode,
                    PartyNotifyOutPackets.BuildPartyNotifyOut2360(handle, m.Gid, m.Value)),
                PartyMessageKind.LeaderChange => m.Gid == 0 ? null : (ZoneS2COpcodes.OnPartyNotifyChangeMaster,
                    PartyStatePackets.BuildOnPartyNotifyChangeMaster2365(handle, m.Gid)),
                PartyMessageKind.Destroy => (PartyStatePackets.OpcodeOnPartyNotifyDestroy,
                    PartyStatePackets.BuildOnPartyNotifyDestroy2358(handle)),
                PartyMessageKind.ExpulsionAck => (ZoneS2COpcodes.OnPartyExpulsion,
                    PartyAnswerPackets.BuildResultAck(PartyAnswerPackets.MsgTagOnPartyExpulsion, requesterHandle, (short)m.Code)),
                PartyMessageKind.ExpulsionNotify => (ZoneS2COpcodes.OnPartyNotifyExpulsion,
                    PartyAnswerPackets.BuildPartyNotifyExpulsion2366(handle, m.Gid)),
                PartyMessageKind.ChangeMasterAck => (ZoneS2COpcodes.OnPartyChangeMaster,
                    PartyAnswerPackets.BuildResultAck(PartyStatePackets.MsgTagOnPartyChangeMaster, requesterHandle, (short)m.Code)),
                PartyMessageKind.AnsTypeChange => (ZoneS2COpcodes.AnsPartyTypeChange,
                    PartyStatePackets.BuildAnsPartyTypeChange(m.Value, (ushort)m.Code)),
                PartyMessageKind.NfyTypeChange => (ZoneS2COpcodes.NfyPartyTypeChange,
                    PartyStatePackets.BuildNfyPartyTypeChange(m.Value)),
                PartyMessageKind.AnsSubMaster => (ZoneS2COpcodes.AnsPartySubMaster,
                    PartyStatePackets.BuildAnsPartySubMaster2702()),
                PartyMessageKind.NfySubMaster => (ZoneS2COpcodes.NfyPartySubMaster,
                    PartyStatePackets.BuildNfyPartySubMaster(m.Gid)),
                PartyMessageKind.AnsDismissSubMaster => (ZoneS2COpcodes.AnsPartyDismissSubMaster,
                    PartyStatePackets.BuildAnsPartyDismissSubMaster2706()),
                PartyMessageKind.NfyDismissSubMaster => (ZoneS2COpcodes.NfyPartyDismissSubmaster,
                    PartyStatePackets.BuildNfyPartyDismissSubmaster(m.Gid)),
                PartyMessageKind.AnsFrameMove => (ZoneS2COpcodes.AnsPartyFrameMove, new byte[] { 0, 0 }),
                PartyMessageKind.NfyFrameMove => (ZoneS2COpcodes.NfyPartyFrameMove,
                    PartyAnswerPackets.BuildNfyPartyFrameMove2705(m.Gid, m.Value, m.Gid2, m.Value2)),
                PartyMessageKind.AnsHelperDeclare => (ZoneS2COpcodes.AnsHelperDeclare,
                    HelperPackets.BuildAnsHelperDeclare2902(m.Gid, m.Value, HelperDeclareResultOk)),
                _ => throw new ArgumentOutOfRangeException(nameof(m), m.Kind, "Unknown party message."),
            };
        }
    }

    /// <summary>Handles a 1382 invitation: <paramref name="inviter"/> invites the target found by the requested name.</summary>
    public async Task InviteToPartyAsync(PlayerState inviter, PlayerState target, string requestedName)
    {
        var inviterGid = MemberGid(inviter);
        var targetGid = MemberGid(target);
        var inviterParty = GetPlayerParty(inviter.SessionId);
        PartyAggregate? inviterAggregate = null;
        if (inviterParty != null)
            lock (inviterParty.Gate) inviterAggregate = Snapshot(inviterParty).Aggregate;
        var targetReachable = _broadcast.GetSession(target.SessionId) != null;

        PartyOutcome outcome;
        IReadOnlyList<PartyEffect> effects;
        lock (_pendingInvites)
        {
            _pendingInvites.TryGetValue(target.SessionId, out var open);
            (outcome, effects) = PartyAggregate.DecideInvite(inviterGid, inviter.Name, targetGid, requestedName,
                inviterAggregate, _playerToParty.ContainsKey(target.SessionId), targetReachable, open?.Invite, _clock.UtcNow);
            if (outcome.Invite is { } invite) _pendingInvites[target.SessionId] = new PendingInvite(inviter.SessionId, invite);
        }

        if (outcome.Result == PartyResult.Refused)
            _logger.LogDebug("{Inviter} invited {Target}, refused with {Code}", inviter.Name, target.Name, outcome.Code);
        await ApplyAsync(null, effects,
            new Dictionary<ulong, uint> { [inviterGid] = inviter.SessionId, [targetGid] = target.SessionId });
        if (outcome.Result == PartyResult.InviteOpened)
            _logger.LogInformation("{Inviter} invited {Target} to party", inviter.Name, target.Name);
    }

    /// <summary>
    /// Handles a 1383 answer from <paramref name="responder"/> to a party invitation. Honored only while the invitation is open.
    /// </summary>
    public async Task AnswerInviteAsync(PlayerState responder, uint responderHandle, ushort answer,
        ulong inviterGid, string inviterName)
    {
        if (!_pendingInvites.TryGetValue(responder.SessionId, out var pending))
        {
            _logger.LogDebug("{Name} answered {Answer} to an invitation that is not open", responder.Name, answer);
            return;
        }

        var responderGid = MemberGid(responder);
        var inviter = _playerManager.GetPlayer(pending.InviterSessionId);
        var inviterParty = inviter == null ? null : GetPlayerParty(inviter.SessionId);
        var (outcome, effects) = PartyAggregate.DecideAnswer(pending.Invite, responderGid,
            inviter == null ? null : MemberGid(inviter), answer, inviterGid, inviterName, inviterParty?.PartyType ?? 0);
        if (outcome.Result == PartyResult.Ignored)
        {
            _logger.LogDebug("{Name} answered a dialog for GID {Gid}, open invitation is from {Inviter}",
                responder.Name, inviterGid, inviter?.Name);
            return;
        }
        if (!_pendingInvites.TryRemove(new KeyValuePair<uint, PendingInvite>(responder.SessionId, pending)))
            return;

        if (inviter == null)
        {
            _logger.LogDebug("{Name} answered an invitation whose inviter is no longer online", responder.Name);
            await ApplyAsync(null, effects, new Dictionary<ulong, uint> { [responderGid] = responder.SessionId },
                responderHandle);
            return;
        }

        if (outcome.Result == PartyResult.JoinNext)
        {
            await JoinPartyAsync(inviter, responder, pending.Invite.InviterPartyId, responderHandle);
            return;
        }

        await ApplyAsync(null, effects, new Dictionary<ulong, uint>
        {
            [responderGid] = responder.SessionId, [MemberGid(inviter)] = inviter.SessionId,
        }, responderHandle);
        if (outcome.Code != 0)
            _logger.LogInformation("{Target} {Verb} {Inviter}'s party invitation", responder.Name,
                answer == PartyAggregate.AnswerDecline ? "declined" : "let time out", inviter.Name);
        else
            _logger.LogDebug("{Name} sent unknown invitation answer {Answer}", responder.Name, answer);
    }

    /// <summary>
    /// Accepted invitation: <paramref name="joiner"/> joins the inviter's party, which is created with the inviter as
    /// leader when needed. A refusal is sent to the joiner as a 2348.
    /// </summary>
    private async Task JoinPartyAsync(PlayerState inviter, PlayerState joiner, uint invitationPartyId, uint responderHandle)
    {
        var inviterGid = MemberGid(inviter);
        var joinerGid = MemberGid(joiner);
        var inviterParty = GetPlayerParty(inviter.SessionId);

        PartyOutcome outcome;
        IReadOnlyList<PartyEffect> effects;
        PartyState? party = inviterParty;
        Dictionary<ulong, uint> keys;
        lock (inviterParty?.Gate ?? new object())
        {
            var snap = inviterParty != null ? Snapshot(inviterParty) : null;
            // An offline member of the party with this GID is taken back in place by re-keying it.
            var staleKey = snap != null && snap.KeyOf.TryGetValue(joinerGid, out var key) && !snap.Online.Contains(joinerGid) ? key : 0u;
            var now = _clock.UtcNow;
            (outcome, effects) = PartyAggregate.DecideJoin(snap?.Aggregate,
                inviterParty == null ? Interlocked.Increment(ref _nextPartyId) : 0, inviterGid, inviter.Name, joinerGid, joiner.Name,
                _playerToParty.ContainsKey(joiner.SessionId), invitationPartyId, snap?.Online ?? new HashSet<ulong>(), now);

            keys = new Dictionary<ulong, uint> { [inviterGid] = inviter.SessionId, [joinerGid] = joiner.SessionId };
            if (outcome.Result is PartyResult.Created or PartyResult.Done)
            {
                if (outcome.Result == PartyResult.Created)
                {
                    party = new PartyState
                    {
                        PartyId = outcome.Party!.Id, LeaderSessionId = inviter.SessionId, FounderGid = inviterGid, CreatedAt = now,
                    };
                    AddMember(party, inviter);
                    _parties[party.PartyId] = party;
                    AddMember(party, joiner);
                }
                else if (staleKey != 0)
                    ReplaceMemberSession(party!, staleKey, joiner);
                else
                    AddMember(party!, joiner);

                var directory = new PartyMemberDirectory(this, party!);
                keys = directory.KeyOf;
                Commit(party!, new PartySnapshot
                {
                    Aggregate = outcome.Party!, Directory = directory, Online = snap?.Online ?? new HashSet<ulong>(),
                    LeaderGidAtStart = outcome.Result == PartyResult.Created ? inviterGid : snap!.LeaderGidAtStart,
                });
            }
        }

        await ApplyAsync(party, effects, keys, responderHandle, joiner);
        if (outcome.Result is PartyResult.Created or PartyResult.Done)
            _logger.LogInformation("{Joiner} joined party {PartyId} (invited by {Inviter})",
                joiner.Name, party!.PartyId, inviter.Name);
        else
            _logger.LogDebug("{Joiner} accepted {Inviter}'s invitation, refused with {Code}", joiner.Name, inviter.Name, outcome.Code);
    }

    public async Task LeavePartyAsync(uint sessionId, PartyLeaveReason reason)
    {
        if (!_playerToParty.TryRemove(sessionId, out var partyId))
            return;

        if (!_parties.TryGetValue(partyId, out var party))
            return;

        PartyOutcome outcome;
        IReadOnlyList<PartyEffect> effects;
        Dictionary<ulong, uint> keys;
        string playerName;
        string? leaderChangedTo = null;
        lock (party.Gate)
        {
            var snap = Snapshot(party);
            var player = party.Members.GetValueOrDefault(sessionId) ?? _playerManager.GetPlayer(sessionId);
            playerName = player?.Name ?? "Unknown";
            (outcome, effects) = snap.Aggregate.DecideLeave(MemberGid(player), ToDomain(reason), snap.Online);
            CommitLeave(party, snap, sessionId, outcome);
            keys = snap.KeyOf;
            if (snap.Aggregate.LeaderGid != snap.LeaderGidAtStart)
                leaderChangedTo = party.Members.GetValueOrDefault(party.LeaderSessionId)?.Name ?? "?";
        }

        await ApplyAsync(party, effects, keys);
        if (outcome.Result == PartyResult.Disbanded)
            _logger.LogInformation("Party {PartyId} disbanded", partyId);
        else if (leaderChangedTo != null)
            _logger.LogInformation("Party {PartyId} leader changed to {Name}", partyId, leaderChangedTo);
        _logger.LogInformation("{Name} left party {PartyId}", playerName, partyId);
    }

    /// <summary>Handles a 1388 expulsion: the leader <paramref name="kickerKey"/> removes the member with the given GID.</summary>
    public async Task KickAsync(uint kickerKey, uint requesterHandle, ulong targetGid)
    {
        var party = GetPlayerParty(kickerKey);
        if (party == null) return;

        PartyOutcome outcome;
        IReadOnlyList<PartyEffect> effects;
        Dictionary<ulong, uint> keys;
        lock (party.Gate)
        {
            var snap = Snapshot(party);
            keys = snap.KeyOf;
            var kickerGid = snap.GidOfKey.GetValueOrDefault(kickerKey);
            (outcome, effects) = snap.Aggregate.DecideKick(kickerGid, targetGid, snap.Online);
            if (outcome.Result is PartyResult.Done or PartyResult.Disbanded)
            {
                var targetKey = snap.KeyOf[targetGid];
                _playerToParty.TryRemove(targetKey, out _);
                CommitLeave(party, snap, targetKey, outcome);
            }
        }

        if (outcome.Result is PartyResult.NotLeader or PartyResult.Ignored) return;
        await ApplyAsync(party, effects, keys, requesterHandle);
        if (outcome.Result == PartyResult.Disbanded)
            _logger.LogInformation("Party {PartyId} disbanded", party.PartyId);
    }

    private async Task MemberWentOfflineAsync(PlayerState departed, bool detachSession)
    {
        var party = GetPlayerParty(departed.SessionId);
        if (party == null) return;

        var gid = MemberGid(departed);

        // Another character was bound to this key; the departed one is not the party's member.
        if (party.Members.TryGetValue(departed.SessionId, out var joined) && MemberGid(joined) != gid)
        {
            _logger.LogDebug("{Name} went offline on session {Id}, which holds another member of party {PartyId}; nothing to lock",
                departed.Name, departed.SessionId, party.PartyId);
            return;
        }

        PartyOutcome outcome;
        IReadOnlyList<PartyEffect> effects;
        Dictionary<ulong, uint> keys;
        lock (party.Gate)
        {
            party.Members[departed.SessionId] = departed;
            if (detachSession) DetachFromSession(party, departed.SessionId);
            if (gid == 0) return;

            var snap = Snapshot(party);
            (outcome, effects) = snap.Aggregate.DecideMemberLocked(gid, _clock.UtcNow);
            if (outcome.Result != PartyResult.Done) return;
            Commit(party, snap);
            keys = snap.KeyOf;
        }

        await ApplyAsync(party, effects, keys);
        _logger.LogInformation("{Name} went offline; party {PartyId} told to lock the member",
            departed.Name, party.PartyId);
    }

    /// <summary>Removes every member locked for at least the lock lifetime at the given time.</summary>
    public async Task<int> ExpireLockedMembersAsync(DateTime nowUtc)
    {
        var dropped = 0;
        foreach (var party in _parties.Values)
        {
            if (party.LockedSince.IsEmpty) continue;
            ulong[] expired;
            lock (party.Gate) expired = [.. Snapshot(party).Aggregate.ExpiredLocks(nowUtc)];
            foreach (var gid in expired)
            {
                if (!_parties.ContainsKey(party.PartyId)) break;
                if (await DropLockedMemberAsync(party, gid, nowUtc)) dropped++;
            }
        }
        return dropped;
    }

    private async Task<bool> DropLockedMemberAsync(PartyState party, ulong gid, DateTime nowUtc)
    {
        PartyOutcome outcome;
        IReadOnlyList<PartyEffect> effects;
        Dictionary<ulong, uint> keys;
        string name;
        lock (party.Gate)
        {
            var snap = Snapshot(party);
            (outcome, effects) = snap.Aggregate.DecideDropLocked(gid, nowUtc, snap.Online);
            if (outcome.Result == PartyResult.Ignored)
            {
                Commit(party, snap);
                return false;
            }
            var key = snap.KeyOf[gid];
            name = party.Members.GetValueOrDefault(key)?.Name ?? "?";
            _playerToParty.TryRemove(key, out _);
            CommitLeave(party, snap, key, outcome);
            keys = snap.KeyOf;
            // The first effect hands the party to an online successor before the member is removed.
            if (effects.Count > 0 && effects[0] is SendToParty { Message.Kind: PartyMessageKind.LeaderChange } && party.Members.GetValueOrDefault(party.LeaderSessionId) is { } successor)
                _logger.LogInformation("Party {PartyId} leader changed to {Name}: the leader stayed away too long", party.PartyId, successor.Name);
        }

        _logger.LogInformation("{Name} was locked for the full lifetime; dropped from party {PartyId}", name, party.PartyId);
        await ApplyAsync(party, effects, keys);
        if (outcome.Result == PartyResult.Disbanded)
            _logger.LogInformation("Party {PartyId} disbanded", party.PartyId);
        return true;
    }

    /// <summary>
    /// Handles a 1614 helper declaration: sets the member's Helper flag (1 declared, 0 withdrawn).
    /// </summary>
    public async Task<HelperDeclareResult> SetHelperFlagAsync(PlayerState member, byte flag)
    {
        var party = GetPlayerParty(member.SessionId);
        if (party == null) return HelperDeclareResult.NotInParty;
        var gid = MemberGid(member);

        PartyOutcome outcome;
        IReadOnlyList<PartyEffect> effects;
        Dictionary<ulong, uint> keys;
        lock (party.Gate)
        {
            var snap = Snapshot(party);
            if (gid == 0 || snap.KeyOf.GetValueOrDefault(gid) != member.SessionId) return HelperDeclareResult.NotInParty;
            (outcome, effects) = snap.Aggregate.DecideSetHelperFlag(gid, flag,
                snap.Directory.LevelsOf(snap.Directory.Gids.Where(g => g != 0)), snap.Online);
            if (outcome.Result == PartyResult.NotMember) return HelperDeclareResult.NotInParty;
            if (outcome.Result == PartyResult.NotEligible) return HelperDeclareResult.NotEligible;
            Commit(party, snap);
            keys = snap.KeyOf;
        }

        await ApplyAsync(party, effects, keys);
        _logger.LogInformation("{Name} set the Helper flag to {Flag} in party {PartyId}", member.Name, flag, party.PartyId);
        return HelperDeclareResult.Done;
    }

    /// <summary>Handles a 1497 party type change requested by a member.</summary>
    public async Task ChangeTypeAsync(uint sessionId, byte partyType, bool requesterOnDungeonMap)
    {
        var party = GetPlayerParty(sessionId);
        if (party == null) return;

        PartyOutcome outcome;
        IReadOnlyList<PartyEffect> effects;
        Dictionary<ulong, uint> keys;
        lock (party.Gate)
        {
            var snap = Snapshot(party);
            (outcome, effects) = snap.Aggregate.DecideChangeType(snap.GidOfKey.GetValueOrDefault(sessionId), partyType, requesterOnDungeonMap);
            if (outcome.Result == PartyResult.NotLeader) return;
            Commit(party, snap);
            keys = snap.KeyOf;
        }

        await ApplyAsync(party, effects, keys);
        if (outcome.Result == PartyResult.Refused)
            _logger.LogInformation("Party {PartyId}: type change to {Type} refused, the leader is on a dungeon map",
                party.PartyId, partyType);
        else
            _logger.LogInformation("Party {PartyId}: type changed to {Type}", party.PartyId, partyType);
    }

    /// <summary>Number of secondary leader slots in a party.</summary>
    public const int MaxSubMasters = PartyAggregate.MaxSubMasters;

    /// <summary>Handles a 1503 request: the leader promotes the named member to secondary leader.</summary>
    public async Task<bool> SetSubMasterAsync(uint sessionId, ulong targetGid)
    {
        var party = GetPlayerParty(sessionId);
        if (party == null) return false;

        PartyOutcome outcome;
        IReadOnlyList<PartyEffect> effects;
        Dictionary<ulong, uint> keys;
        lock (party.Gate)
        {
            var snap = Snapshot(party);
            (outcome, effects) = snap.Aggregate.DecideSetSubMaster(snap.GidOfKey.GetValueOrDefault(sessionId), targetGid);
            Commit(party, snap);
            keys = snap.KeyOf;
        }
        if (outcome.Result != PartyResult.Done) return false;

        await ApplyAsync(party, effects, keys);
        _logger.LogInformation("Party {PartyId}: GID {Gid} made a secondary leader", party.PartyId, targetGid);
        return true;
    }

    /// <summary>Handles a 1505 request: the leader removes the secondary leader role from the named member.</summary>
    public async Task<bool> DismissSubMasterAsync(uint sessionId, ulong targetGid)
    {
        var party = GetPlayerParty(sessionId);
        if (party == null) return false;

        PartyOutcome outcome;
        IReadOnlyList<PartyEffect> effects;
        Dictionary<ulong, uint> keys;
        lock (party.Gate)
        {
            var snap = Snapshot(party);
            (outcome, effects) = snap.Aggregate.DecideDismissSubMaster(snap.GidOfKey.GetValueOrDefault(sessionId), targetGid);
            Commit(party, snap);
            keys = snap.KeyOf;
        }
        if (outcome.Result != PartyResult.Done) return false;

        await ApplyAsync(party, effects, keys);
        _logger.LogInformation("Party {PartyId}: GID {Gid} is no longer a secondary leader", party.PartyId, targetGid);
        return true;
    }

    /// <summary>
    /// Handles a 1387 request: the leader hands leadership to the named member. <paramref name="refusedAsWoe"/> is the
    /// handler's WoE-map verdict for that member, answered with 24324.
    /// </summary>
    public async Task ChangeLeaderAsync(uint sessionId, uint requesterHandle, ulong newLeaderGid, bool refusedAsWoe = false)
    {
        var party = GetPlayerParty(sessionId);
        if (party == null) return;

        PartyOutcome outcome;
        IReadOnlyList<PartyEffect> effects;
        Dictionary<ulong, uint> keys;
        lock (party.Gate)
        {
            var snap = Snapshot(party);
            (outcome, effects) = snap.Aggregate.DecideChangeLeader(snap.GidOfKey.GetValueOrDefault(sessionId), newLeaderGid,
                snap.Online, refusedAsWoe);
            if (outcome.Result == PartyResult.Done) Commit(party, snap);
            keys = snap.KeyOf;
        }

        if (outcome.Result == PartyResult.NotLeader) return;
        if (outcome.Result == PartyResult.NotMember)
        {
            _logger.LogDebug("Party {PartyId}: leader change to GID {Gid} names no online member", party.PartyId, newLeaderGid);
            return;
        }
        await ApplyAsync(party, effects, keys, requesterHandle);
        if (outcome.Result == PartyResult.Done)
            _logger.LogInformation("Party {PartyId}: leader changed to GID {Gid}", party.PartyId, newLeaderGid);
    }

    /// <summary>
    /// Handles a 1504 frame move: the mover takes <paramref name="targetSlot"/> and the occupant (0 for whoever the
    /// server holds there) takes the mover's old slot, then the requester gets the 1504 answer and every member the 2705 notice.
    /// </summary>
    public async Task SwapFrameSlotsAsync(PartyState party, uint requesterKey, ulong movedGid, ulong occupantGid, byte targetSlot)
    {
        PartyOutcome outcome;
        IReadOnlyList<PartyEffect> effects;
        Dictionary<ulong, uint> keys;
        lock (party.Gate)
        {
            var snap = Snapshot(party);
            (outcome, effects) = snap.Aggregate.DecideSwapFrameSlots(snap.GidOfKey.GetValueOrDefault(requesterKey), movedGid,
                occupantGid, targetSlot);
            if (outcome.Result != PartyResult.Done) return;
            Commit(party, snap);
            keys = snap.KeyOf;
        }

        await ApplyAsync(party, effects, keys);
    }
}
