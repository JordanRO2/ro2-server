using RO2.GameData.Tables;

namespace RO2.DungeonFinder.Domain;

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

/// <summary>One member's place in a round: its seat and the role it holds (0 for none yet).</summary>
public sealed class FinderSeat(SeatKey key)
{
    public SeatKey Key { get; } = key;
    public uint SessionId => Key.SessionId;
    public byte Role { get; internal set; }
}

/// <summary>
/// One party role round: every online member of a party asked for a role on a five-player list. Methods change the
/// round and return what to send, in order; the host keeps the live rounds, the lock and the frames.
/// </summary>
public sealed class FinderRound
{
 /// <summary>The 2834 and search-answer result for an accepted request.</summary>
    public const ushort ResultAccepted = 0;
 /// <summary>The 2834 result for a role whose slot is already filled.</summary>
    public const ushort ResultSlotTaken = 8;

    private readonly List<FinderSeat> _seats;

    private FinderRound(long id, uint partyId, FindDungeonList row, List<FinderSeat> seats)
    {
        Id = id;
        PartyId = partyId;
        Row = row;
        _seats = seats;
    }

    public long Id { get; }
    public uint PartyId { get; }
    public FindDungeonList Row { get; }
    public IReadOnlyList<FinderSeat> Seats => _seats;

    public bool AllFilled => _seats.TrueForAll(s => s.Role != 0);

    public bool Has(SeatKey key) => _seats.Exists(s => s.Key == key);

 /// <summary>
 /// Opens a round with <paramref name="searcher"/> first and <paramref name="others"/> after it, placing the
 /// searcher's role. The round is null when none of <paramref name="searcherFlags"/>' roles has a free slot.
 /// </summary>
    public static (FinderRound? Round, OpenStatus Status, IReadOnlyList<FinderEffect> Effects) TryOpen(
        long id, uint partyId, FindDungeonList row, SeatKey searcher, IReadOnlyList<SeatKey> others,
        byte searcherFlags, SearchKind kind)
    {
        var seats = new List<FinderSeat>(others.Count + 1) { new(searcher) };
        foreach (var other in others)
            seats.Add(new FinderSeat(other));
        var role = RoleSlotting.FirstFreeRole(row, FilledOf(seats), searcherFlags);
        if (role == 0)
            return (null, OpenStatus.SearcherHasNoSlot, []);
        seats[0].Role = role;

        var round = new FinderRound(id, partyId, row, seats);
        var effects = new List<FinderEffect> { new SearchAnswered(searcher.SessionId, ResultAccepted, kind) };
        foreach (var seat in seats.Skip(1))
            effects.Add(new SelectRolePlease(seat.SessionId));
        round.AddState(effects);
        return (round, OpenStatus.Opened, effects);
    }

 /// <summary>
 /// Places <paramref name="member"/> with <paramref name="flags"/>' first free role. SlotTaken and Completed end the
 /// round; the host removes it.
 /// </summary>
    public (SelectStatus Status, byte Role, IReadOnlyList<FinderEffect> Effects) SelectRole(SeatKey member, byte flags)
    {
        var seat = _seats.Find(s => s.Key == member)!;
        seat.Role = 0;
        var role = RoleSlotting.FirstFreeRole(Row, FilledOf(_seats), flags);
        var effects = new List<FinderEffect>();
        if (role == 0)
        {
            effects.Add(new SelectRoleAnswered(member.SessionId, ResultSlotTaken));
            effects.AddRange(Fail());
            return (SelectStatus.SlotTaken, 0, effects);
        }

        seat.Role = role;
        effects.Add(new SelectRoleAnswered(member.SessionId, ResultAccepted));
        AddState(effects);
        return (AllFilled ? SelectStatus.Completed : SelectStatus.Accepted, role, effects);
    }

 /// <summary>The closing notice for every member.</summary>
    public IReadOnlyList<FinderEffect> Fail() => _seats.Select(s => (FinderEffect)new RoundFailed(s.SessionId)).ToList();

    private static RoleCounts FilledOf(IEnumerable<FinderSeat> seats) => RoleCounts.Of(seats.Select(s => s.Role));

 /// <summary>Adds the round's counts for every member, each with its own role.</summary>
    private void AddState(List<FinderEffect> effects)
    {
        var filled = FilledOf(_seats);
        foreach (var s in _seats)
            effects.Add(new InProgress(s.SessionId, 0, filled.Tanks, filled.Healers, filled.Dealers, s.Role));
    }
}
