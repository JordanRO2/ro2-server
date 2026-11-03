using RO2.DungeonFinder.Application;
using RO2.DungeonFinder.Domain;
using RO2.GameData.Tables;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// The dungeon finder's party role round. A party member's search for a five-player list asks every other
/// online member for a role. The round ends when every member holds a role, a role's slot is already filled, a
/// member cancels, or <see cref="SelectRoleTimeout"/> passes with a role still unpicked. One round per party.
/// Each method returns the frames to send, in order.
/// </summary>
/// <remarks>
/// <para>
/// Each member answers once with its role. A member that cancels, or lets the client's role window expire
/// without answering, ends the round for every member, who receive a closing notice.
/// </para>
/// <para>
/// Slots are taken in tank, healer, dealer order, up to the round row's role quotas.
/// </para>
/// </remarks>
public sealed class DungeonFinderPartyRound
{
 /// <summary>The rounds shared by the Zone handlers.</summary>
    public static DungeonFinderPartyRound Shared { get; } = new();

 /// <summary>How long the role window stays open on the client.</summary>
    public static readonly TimeSpan ClientRoleCheckWindow = TimeSpan.FromSeconds(40);

 /// <summary>
 /// How long an unfinished round lasts, counted from the search. It is the client's role window plus one second,
 /// so that an answer made in the window's last moment still arrives before the round expires.
 /// </summary>
    public static readonly TimeSpan SelectRoleTimeout = ClientRoleCheckWindow + TimeSpan.FromSeconds(1);

 /// <summary>One S2C frame for one session.</summary>
    public readonly record struct Outgoing(uint SessionId, ushort Opcode, byte[] Body);

 /// <summary>
 /// Returns true when <paramref name="player"/> is still in the world on <paramref name="sessionId"/> and still a
 /// member of party <paramref name="partyId"/>.
 /// </summary>
    public delegate bool SeatCheck(uint partyId, SeatKey seat);

 /// <summary>The result of a party search.</summary>
    public enum OpenStatus
    {
 /// <summary>The round is open. The searcher and every member have been sent their prompts.</summary>
        Opened,
 /// <summary>The party already has an open round; nothing was opened.</summary>
        AlreadyOpen,
 /// <summary>None of the searcher's roles has a free slot in the row; nothing was opened.</summary>
        SearcherHasNoSlot,
    }

 /// <summary>The result of a role answer.</summary>
    public enum SelectStatus
    {
 /// <summary>The member holds a role; the round waits for the others.</summary>
        Accepted,
 /// <summary>The member holds a role and so does everyone else; the round is closed.</summary>
        Completed,
 /// <summary>No role of the member's has a free slot; the round is dropped.</summary>
        SlotTaken,
 /// <summary>The member is in no open round.</summary>
        NoRound,
    }

 /// <summary>
 /// The frames to send for a party search, and the outcome. <paramref name="RoundId"/> identifies the opened
 /// round for <see cref="Expire"/>, and is 0 when no round was opened.
 /// </summary>
    public sealed record OpenOutcome(OpenStatus Status, byte SearcherRole, long RoundId, List<Outgoing> Sends);

 /// <summary>The frames to send for a role answer, and the outcome.</summary>
    public sealed record SelectOutcome(SelectStatus Status, byte Role, List<Outgoing> Sends);

 /// <summary>
 /// The frames to send when a round is ended by a cancel or by a timeout, and whether that round ended.
 /// </summary>
    public sealed record CancelOutcome(bool Ended, List<Outgoing> Sends);

    private sealed class Seat(SeatKey key)
    {
        public SeatKey Key { get; } = key;
        public uint SessionId => Key.SessionId;
        public byte Role { get; set; }
    }

    private sealed class Round(long id, uint partyId, FindDungeonList row, List<Seat> seats)
    {
        public long Id { get; } = id;
        public uint PartyId { get; } = partyId;
        public FindDungeonList Row { get; } = row;
        public List<Seat> Seats { get; } = seats;
    }

    private readonly Lock _gate = new();
    private readonly List<Round> _rounds = [];
    private long _lastRoundId;

 /// <summary>
 /// Opens party <paramref name="partyId"/>'s round for a search by <paramref name="searcher"/> on the quotas of
 /// <paramref name="row"/>. The searcher's role is placed at once; the other online members are asked for theirs.
 /// </summary>
    public OpenOutcome Open(uint partyId, FindDungeonList row, PlayerState searcher, byte searcherFlags,
        IReadOnlyList<PlayerState> others, SeatCheck isSeated,
        ushort searcherAnswerOpcode = ZoneS2COpcodes.AnsPartyMatchingFindDungeon)
    {
        ArgumentOutOfRangeException.ThrowIfZero(others.Count);
        lock (_gate)
        {
            var effects = PruneLocked(isSeated, out _);
            if (_rounds.Exists(r => r.PartyId == partyId))
                return new OpenOutcome(OpenStatus.AlreadyOpen, 0, 0, Translate(effects));

            var seats = new List<Seat>(others.Count + 1) { new(SeatKeys.Of(searcher)) };
            foreach (var member in others)
                seats.Add(new Seat(SeatKeys.Of(member)));
            var role = FirstFreeRole(row, seats, searcherFlags);
            if (role == 0)
                return new OpenOutcome(OpenStatus.SearcherHasNoSlot, 0, 0, Translate(effects));
            seats[0].Role = role;

            var round = new Round(++_lastRoundId, partyId, row, seats);
            _rounds.Add(round);
            effects.Add(new SearchAnswered(searcher.SessionId, DungeonFinderPackets.ResultAccepted,
                searcherAnswerOpcode == ZoneS2COpcodes.AnsPartyMatchingFindDungeonRandom ? SearchKind.Random : SearchKind.Normal));
            foreach (var seat in seats.Skip(1))
                effects.Add(new SelectRolePlease(seat.SessionId));
            AddStateLocked(round, effects);
            return new OpenOutcome(OpenStatus.Opened, role, round.Id, Translate(effects));
        }
    }

 /// <summary>
 /// Places <paramref name="member"/> in its round with <paramref name="roleFlags"/>, or ends the round when none of
 /// those roles has a free slot.
 /// </summary>
    public SelectOutcome SelectRole(PlayerState member, byte roleFlags, SeatCheck isSeated)
    {
        lock (_gate)
        {
            var effects = PruneLocked(isSeated, out var told);
            var round = RoundOfLocked(member);
            if (round is null)
            {
                
                effects.Add(new SelectRoleAnswered(member.SessionId, FindPartyRolePackets.SelectRoleSlotTaken));
                if (!told.Contains(member.SessionId))
                    effects.Add(new RoundFailed(member.SessionId));
                return new SelectOutcome(SelectStatus.NoRound, 0, Translate(effects));
            }

            var memberKey = SeatKeys.Of(member);
            var seat = round.Seats.Find(s => s.Key == memberKey)!;
            seat.Role = 0; 
            var role = FirstFreeRole(round.Row, round.Seats, roleFlags);
            if (role == 0)
            {
                _rounds.Remove(round);
                effects.Add(new SelectRoleAnswered(member.SessionId, FindPartyRolePackets.SelectRoleSlotTaken));
                foreach (var s in round.Seats)
                    effects.Add(new RoundFailed(s.SessionId));
                return new SelectOutcome(SelectStatus.SlotTaken, 0, Translate(effects));
            }

            seat.Role = role;
            effects.Add(new SelectRoleAnswered(member.SessionId, FindPartyRolePackets.SelectRoleAccepted));
            AddStateLocked(round, effects);
            if (!round.Seats.TrueForAll(s => s.Role != 0))
                return new SelectOutcome(SelectStatus.Accepted, role, Translate(effects));
            _rounds.Remove(round);
            return new SelectOutcome(SelectStatus.Completed, role, Translate(effects));
        }
    }

 /// <summary>
 /// Ends the member's round, if it is in one, and tells every member. The canceller's own answer is sent by the
 /// caller.
 /// </summary>
    public CancelOutcome Cancel(PlayerState member, SeatCheck isSeated)
    {
        lock (_gate)
        {
            var effects = PruneLocked(isSeated, out _);
            if (RoundOfLocked(member) is not { } round)
                return new CancelOutcome(false, Translate(effects));
            _rounds.Remove(round);
            foreach (var s in round.Seats)
                effects.Add(new RoundFailed(s.SessionId));
            return new CancelOutcome(true, Translate(effects));
        }
    }

 /// <summary>
 /// Ends round <paramref name="roundId"/> when it is still open after its timeout, and tells every member. A
 /// round that already ended, and any later round of the same party, are left alone.
 /// </summary>
    public CancelOutcome Expire(long roundId, SeatCheck isSeated)
    {
        lock (_gate)
        {
            var round = _rounds.Find(r => r.Id == roundId);
            var effects = PruneLocked(isSeated, out _);
            if (round is null)
                return new CancelOutcome(false, Translate(effects));
            if (_rounds.Remove(round))
                foreach (var s in round.Seats)
                    effects.Add(new RoundFailed(s.SessionId));
            return new CancelOutcome(true, Translate(effects));
        }
    }

 /// <summary>
 /// The live membership check used by the handlers: the session still carries this character and the character is
 /// still in the party. A member that went offline or left fails it.
 /// </summary>
    public static SeatCheck SeatCheckFor(IFinderPartyView view, ISeatLiveness liveness)
        => (partyId, seat) => liveness.IsLive(seat) && view.IsStillMember(partyId, seat);

 /// <summary>Sends <paramref name="sends"/> in order through the unencrypted game RMI path.</summary>
    public static async Task DeliverAsync(IEnumerable<Outgoing> sends, Func<uint, ProudSession?> sessionOf)
    {
        foreach (var o in sends)
            if (sessionOf(o.SessionId) is { } target)
                await target.SendInternalRmiAsync(o.Opcode, o.Body);
    }

 /// <summary>
 /// The searcher's <c>[u16 result]</c> answer on <paramref name="opcode"/>: 2805 for a random search, otherwise
 /// 2804.
 /// </summary>
    internal static byte[] SearcherAnswer(ushort opcode, ushort result)
        => opcode == ZoneS2COpcodes.AnsPartyMatchingFindDungeonRandom
            ? DungeonFinderPackets.BuildFindDungeonRandomAnswer2805(result)
            : DungeonFinderPackets.BuildFindDungeonAnswer2804(result);

 /// <summary>The round's filled slots.</summary>
    private static RoleCounts Filled(IReadOnlyList<Seat> seats) => RoleCounts.Of(seats.Select(s => s.Role));

 /// <summary>The first of <paramref name="flags"/>' roles, in tank, healer, dealer order, with a free slot; 0 for none.</summary>
    private static byte FirstFreeRole(FindDungeonList row, IReadOnlyList<Seat> seats, byte flags)
        => RoleSlotting.FirstFreeRole(row, Filled(seats), flags);

 /// <summary>Sends the round's counts to every member, each with its own role.</summary>
    private static void AddStateLocked(Round round, List<FinderEffect> effects)
    {
        var filled = Filled(round.Seats);
        foreach (var s in round.Seats)
            effects.Add(new InProgress(s.SessionId, 0, filled.Tanks, filled.Healers, filled.Dealers, s.Role));
    }

    private static List<Outgoing> Translate(List<FinderEffect> effects)
        => effects.Select(FinderEffectTranslator.ToFrame).ToList();

    private Round? RoundOfLocked(PlayerState member)
    {
        var key = SeatKeys.Of(member);
        return _rounds.Find(r => r.Seats.Exists(s => s.Key == key));
    }

 /// <summary>
 /// Drops every round with a member that <paramref name="isSeated"/> rejects, and returns a closing notice for each
 /// member of those rounds still in the world. <paramref name="told"/> receives their sessions.
 /// </summary>
    private List<FinderEffect> PruneLocked(SeatCheck isSeated, out HashSet<uint> told)
    {
        var sends = new List<FinderEffect>();
        told = [];
        for (int i = 0; i < _rounds.Count;)
        {
            var round = _rounds[i];
            var present = round.Seats.FindAll(s => isSeated(round.PartyId, s.Key));
            if (present.Count == round.Seats.Count)
            {
                i++;
                continue;
            }
            _rounds.RemoveAt(i);
            foreach (var s in present)
            {
                sends.Add(new RoundFailed(s.SessionId));
                told.Add(s.SessionId);
            }
        }
        return sends;
    }
}
