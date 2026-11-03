namespace RO2.PartyDice.Domain;

/// <summary>A participant of a dice round: the session that was asked and the character GID it was asked as.</summary>
public readonly record struct DiceSeat(uint SessionId, ulong Gid);

/// <summary>
/// One party loot roll for a corpse item: who was asked, who has answered with what, and whether it has settled.
/// Pure decisions: the host rolls the dice and reads the party, this takes the result and returns the effects to
/// apply, in order. Not thread-safe, the host serializes calls on one round.
/// </summary>
public sealed class DiceRound
{
    private sealed class Entry(DiceSeat seat)
    {
        public readonly DiceSeat Seat = seat;
        public bool Answered;
        public bool Rolled;
        public ushort Roll;
        public long AnswerOrder;
    }

    private readonly List<Entry> _entries;
    private long _nextOrder;

    public DiceRound(IEnumerable<DiceSeat> seats) => _entries = [.. seats.Select(s => new Entry(s))];

    /// <summary>The participants, in the order they were asked.</summary>
    public IReadOnlyList<DiceSeat> Seats => [.. _entries.Select(e => e.Seat)];

    /// <summary>True once the round has settled; later answers and settles do nothing.</summary>
    public bool Settled { get; private set; }

    /// <summary>The roll window opens on every participant.</summary>
    public IReadOnlyList<DiceEffect> Open() => [new Opened([.. _entries.Select(e => e.Seat.SessionId)])];

    /// <summary>
    /// Records a participant's answer: a roll (<paramref name="rolled"/>, with the value the host drew) or a pass.
    /// Nothing once settled, from a non-participant or for a second answer; otherwise <see cref="Answered"/> or
    /// <see cref="Refused"/>. The first answer takes the earliest answer order, which breaks a tie between equal rolls.
    /// </summary>
    public IReadOnlyList<DiceEffect> Answer(uint sessionId, ulong gid, bool rolled, ushort roll)
    {
        var me = _entries.FirstOrDefault(e => e.Seat.SessionId == sessionId && e.Seat.Gid == gid);
        if (Settled || me == null || me.Answered) return [];
        me.Answered = true;
        me.Rolled = rolled;
        me.Roll = roll;
        me.AnswerOrder = _nextOrder++;
        return [rolled ? new Answered(gid, roll) : new Refused(gid)];
    }

    /// <summary>
    /// Settles the round once every participant still taking part (<paramref name="stillIn"/>) has answered, or when
    /// <paramref name="timedOut"/>. Idempotent: a settled round, or one still waiting on a participant, returns
    /// nothing. Only the rolls of participants still in count; the highest wins (earliest answer on a tie) and is
    /// followed by <see cref="HoldDiceBound"/>. With no roll the all-pass notice is followed by
    /// <see cref="ReleaseDiceBound"/>.
    /// </summary>
    public IReadOnlyList<DiceEffect> Settle(Func<DiceSeat, bool> stillIn, bool timedOut)
    {
        if (Settled) return [];
        if (!timedOut && _entries.Any(e => !e.Answered && stillIn(e.Seat))) return [];
        Settled = true;
        var rolls = _entries.Where(e => e.Rolled && stillIn(e.Seat)).ToArray();
        int best = PartyDiceRolls.PickWinner([.. rolls.Select(e => (e.Roll, e.AnswerOrder))]);
        if (best < 0) return [new AllPassed(), new ReleaseDiceBound()];
        return [new Winner(rolls[best].Seat.SessionId, rolls[best].Seat.Gid, rolls[best].Roll), new HoldDiceBound()];
    }
}
