using RO2.Parties.Application;
using RO2.PartyDice.Application;
using RO2.PartyDice.Domain;
using Xunit;

namespace RO2.Tests.Dice;

/// <summary>The dice round aggregate and the Application glue around it (rolling, roster mapping), with no host and no sessions.</summary>
public class PartyDiceDomainTests
{
    private static readonly DiceSeat A = new(1, 0xA1), B = new(2, 0xB2), C = new(3, 0xC3);

    private static DiceRound Round() => new([A, B, C]);

    private static DiceEffect[] E(params DiceEffect[] effects) => effects;

    private static bool Everyone(DiceSeat _) => true;

    [Fact]
    public void Open_NamesEverySessionInTheOrderTheyWereAsked()
        => Assert.Equal(new uint[] { 1, 2, 3 }, Assert.IsType<Opened>(Assert.Single(Round().Open())).ToSessionIds);

    [Fact]
    public void Answer_RecordsOnce_AndIgnoresAStrangerAWrongGidAndASecondAnswer()
    {
        var round = Round();
        Assert.Equal(E(new Answered(A.Gid, 1500)), round.Answer(A.SessionId, A.Gid, true, 1500));
        Assert.Equal(E(new Refused(B.Gid)), round.Answer(B.SessionId, B.Gid, false, 0));
        Assert.Empty(round.Answer(A.SessionId, A.Gid, true, 1999));      // second answer
        Assert.Empty(round.Answer(9, 0x99, true, 1999));                 // not a participant
        Assert.Empty(round.Answer(C.SessionId, 0xDEAD, true, 1999));     // right session, other character
    }

    [Fact]
    public void Settle_WaitsForTheParticipantsStillInUnlessTimedOut()
    {
        var round = Round();
        round.Answer(A.SessionId, A.Gid, true, 1200);
        round.Answer(B.SessionId, B.Gid, false, 0);
        Assert.Empty(round.Settle(Everyone, timedOut: false));           // C has not answered
        Assert.False(round.Settled);
        Assert.Equal(E(new Winner(1, A.Gid, 1200), new HoldDiceBound()), round.Settle(Everyone, timedOut: true));
    }

    [Fact]
    public void Settle_DoesNotWaitForAParticipantWhoLeft()
    {
        var round = Round();
        round.Answer(A.SessionId, A.Gid, true, 1200);
        round.Answer(B.SessionId, B.Gid, true, 300);
        Assert.Equal(E(new Winner(1, A.Gid, 1200), new HoldDiceBound()), round.Settle(s => s != C, timedOut: false));
    }

    [Fact]
    public void Settle_IsIdempotent_AndLaterAnswersDoNothing()
    {
        var round = Round();
        round.Answer(A.SessionId, A.Gid, true, 1200);
        Assert.NotEmpty(round.Settle(Everyone, timedOut: true));
        Assert.True(round.Settled);
        Assert.Empty(round.Settle(Everyone, timedOut: true));
        Assert.Empty(round.Settle(Everyone, timedOut: false));
        Assert.Empty(round.Answer(B.SessionId, B.Gid, true, 2000));
    }

    [Fact]
    public void Settle_CountsOnlyTheRollsOfParticipantsStillIn()
    {
        var round = Round();
        round.Answer(A.SessionId, A.Gid, true, 1990);
        round.Answer(B.SessionId, B.Gid, true, 400);
        round.Answer(C.SessionId, C.Gid, false, 0);
        Assert.Equal(E(new Winner(2, B.Gid, 400), new HoldDiceBound()), round.Settle(s => s != A, timedOut: false));
    }

    [Fact]
    public void Settle_TieGoesToTheEarliestAnswer_NotTheEarliestSeat()
    {
        var round = Round();
        round.Answer(C.SessionId, C.Gid, true, 1500);
        round.Answer(A.SessionId, A.Gid, true, 1500);
        round.Answer(B.SessionId, B.Gid, true, 1500);
        Assert.Equal(E(new Winner(3, C.Gid, 1500), new HoldDiceBound()), round.Settle(Everyone, timedOut: false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AllPassed_ReleasesTheDiceMark_WhateverTheWayItSettled(bool timedOut)
    {
        var round = Round();
        round.Answer(A.SessionId, A.Gid, false, 0);
        round.Answer(B.SessionId, B.Gid, false, 0);
        round.Answer(C.SessionId, C.Gid, false, 0);
        Assert.Equal(E(new AllPassed(), new ReleaseDiceBound()), round.Settle(Everyone, timedOut));
    }

    [Fact]
    public void NobodyAnswering_TimesOutToAnAllPass_AndAWinnerWhoLeftIsAnAllPass()
    {
        Assert.Equal(E(new AllPassed(), new ReleaseDiceBound()), Round().Settle(Everyone, timedOut: true));

        var round = Round();
        round.Answer(A.SessionId, A.Gid, true, 1500);
        round.Answer(B.SessionId, B.Gid, false, 0);
        round.Answer(C.SessionId, C.Gid, false, 0);
        Assert.Equal(E(new AllPassed(), new ReleaseDiceBound()), round.Settle(s => s != A, timedOut: false));
    }

    [Fact]
    public void RollingAnAnswer_DrawsFromTheGivenRandom_AndAnUnknownChoiceRecordsNothing()
    {
        var round = Round();
        Assert.Empty(DiceRolling.Answer(round, A.SessionId, A.Gid, 7));
        var expected = (ushort)new Random(5).Next(1001, 2001);
        Assert.Equal(E(new Answered(A.Gid, expected)),
            DiceRolling.Answer(round, A.SessionId, A.Gid, PartyDiceRolls.ChoiceHigh, new Random(5)));
        Assert.Equal(E(new Refused(B.Gid)), DiceRolling.Answer(round, B.SessionId, B.Gid, PartyDiceRolls.ChoicePass));
        var low = Assert.IsType<Answered>(Assert.Single(DiceRolling.Answer(round, C.SessionId, C.Gid, PartyDiceRolls.ChoiceLow)));
        Assert.InRange(low.Roll, PartyDiceRolls.LowMin, PartyDiceRolls.LowMax);
    }

    [Theory]
    [InlineData(1, 100, true)]
    [InlineData(5, 5, true)]
    [InlineData(1, 65535, true)]
    [InlineData(0, 10, false)]
    [InlineData(100, 1, false)]
    public void ChatDice_RollsInsideAnAnswerableRangeOnly(int min, int max, bool answered)
    {
        Assert.Equal(answered, DiceRolling.TryRollChat((ushort)min, (ushort)max, out var roll));
        if (answered) Assert.InRange(roll, (ushort)min, (ushort)max);
        else Assert.Equal(0, roll);
        Assert.Equal(answered, PartyDiceRolls.IsChatRangeAnswerable((ushort)min, (ushort)max));
    }

    private sealed class FakeRoster(PartyRoster? party) : IPartyRoster
    {
        public PartyRoster? RosterOf(uint sessionId) => party;
        public PartyRoster? RosterOfParty(uint partyId) => party is not null && party.PartyId == partyId ? party : null;
        public bool IsStillMember(uint partyId, uint sessionId)
            => RosterOfParty(partyId)?.Members.Any(m => m.SessionId == sessionId) == true;
    }

    private static PartyRoster Party(params PartyMember[] members) => new(7, 0xA1, 1, members);

    [Fact]
    public void Seats_AreTheOnlineMembersOnTheCorpseMapWithASession_InRosterOrder()
    {
        var roster = Party(
            new PartyMember(1, 11, 0xA1, "A", 1100, true),
            new PartyMember(2, 12, 0xB2, "B", 1100, false),   // offline
            new PartyMember(3, 13, 0xC3, "C", 2200, true),    // other map
            new PartyMember(4, 14, 0xD4, "D", 1100, true),    // no session
            new PartyMember(5, 15, 0xE5, "E", 1100, true));
        Assert.Equal(new[] { new DiceSeat(1, 0xA1), new DiceSeat(5, 0xE5) }, PartyDiceRoster.Seats(roster, 1100, id => id != 4));
        Assert.Empty(PartyDiceRoster.Seats(null, 1100, _ => true));
    }

    [Fact]
    public void StillIn_NeedsTheSamePartyTheSameCharacterAndOnline()
    {
        var roster = Party(
            new PartyMember(1, 11, 0xA1, "A", 1100, true),
            new PartyMember(2, 12, 0xB2, "B", 1100, false),
            new PartyMember(3, 13, 0xFF, "C2", 1100, true));  // session 3 now plays another character
        var stillIn = PartyDiceRoster.StillIn(new FakeRoster(roster), 7);
        Assert.True(stillIn(new DiceSeat(1, 0xA1)));
        Assert.False(stillIn(new DiceSeat(2, 0xB2)));
        Assert.False(stillIn(new DiceSeat(3, 0xC3)));
        Assert.False(stillIn(new DiceSeat(9, 0x99)));
        Assert.False(PartyDiceRoster.StillIn(new FakeRoster(roster), 8)(new DiceSeat(1, 0xA1)));   // party gone
    }
}
