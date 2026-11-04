using RO2.DungeonFinder.Domain;
using System.Text.Json;
using RO2.GameData.Tables;
using RO2.Network.Protocol;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Tests the dungeon finder party role round: the 1555 search by a party member, the 2822, 1568, 1574 and
/// 1558 requests, the timeout, and the 2834, 2806 and 2831 messages.
/// </summary>
/// <remarks>
/// Member-side frames are asserted directly. The searcher's and the other members' frames are checked for
/// consistency with the round model (<see cref="DungeonFinderPartyRound"/>). The quotas come from
/// data/Find_Dungeon_List.json.
/// </remarks>
public class DungeonFinderPartyRoundTests
{
    private const uint Party = 7;
    private const uint SearcherSession = 10, MSession = 11, XSession = 12, YSession = 13;

    private static readonly DungeonFinderPartyRound.SeatCheck Everyone = (_, _) => true;

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    private static string Frame(uint session, ushort opcode, string body) => $"{session}:{opcode}:{body}";

    private static string[] Render(IEnumerable<DungeonFinderPartyRound.Outgoing> sends)
        => sends.Select(o => Frame(o.SessionId, o.Opcode, Hex(o.Body))).ToArray();

    private static PlayerState Player(uint session) => new() { SessionId = session, Name = $"p{session}" };

    private static string[] To(uint session, IEnumerable<DungeonFinderPartyRound.Outgoing> sends)
        => Render(sends.Where(o => o.SessionId == session));

    
    private static readonly ushort A2804 = ZoneS2COpcodes.AnsPartyMatchingFindDungeon;
    private static readonly ushort A2806 = ZoneS2COpcodes.AnsPartyMatchingInProgressInfo;
    private static readonly ushort N2822 = ZoneS2COpcodes.NfyFindParty_Party_Dungeon_SelectRolePlease;
    private static readonly ushort A2834 = ZoneS2COpcodes.AnsFindParty_Party_SelectRole;
    private static readonly ushort N2831 = ZoneS2COpcodes.NfyResult_FindParty_Party_SelectRole;

 /// <summary>Loads data/Find_Dungeon_List.json.</summary>
    private static List<FindDungeonList> Rows()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "data", "Find_Dungeon_List.json")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return JsonSerializer.Deserialize<List<FindDungeonList>>(
            File.ReadAllText(Path.Combine(dir.FullName, "data", "Find_Dungeon_List.json")), JsonOptions)!;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

 /// <summary>List 4 (Wolf Den Hard, Party_Num 5).</summary>
    private static FindDungeonList Five => Rows().Single(r => r.List_ID == 4);

    [DataFact]
    public void EveryPartyNumFiveRow_HasTheOneOneThreeQuotasTheRoundUses()
    {
        var five = Rows().Where(r => r.Party_Num == 5).ToList();
        Assert.Equal(20, five.Count);
        Assert.All(five, r => Assert.Equal("1/1/3", $"{r.Role_Tanker}/{r.Role_Healer}/{r.Role_Dealer}"));
    }

 /// <summary>
 /// A dealer searches while the member and another party member are online. The searcher gets 2804 0 and a
 /// 2806 counting itself; the member and the other player get an empty 2822 and a 2806 with own role 0. The
 /// member's 2806 is the round opener <c>00000000000000010000</c>.
 /// </summary>
    [DataFact]
    public void Open_AsksTheOthersAndShowsEveryoneTheRound()
    {
        var round = new DungeonFinderPartyRound();
        var searcher = Player(SearcherSession);

        var opened = round.Open(Party, Five, SeatKeys.Of(searcher), RoleKind.RoleDealer,
            [SeatKeys.Of(Player(MSession)), SeatKeys.Of(Player(XSession))], Everyone);

        Assert.Equal(OpenStatus.Opened, opened.Status);
        Assert.Equal(RoleKind.RoleDealer, opened.SearcherRole);
        Assert.Equal(
            [
                Frame(SearcherSession, A2804, "0000"),
                Frame(MSession, N2822, ""),
                Frame(XSession, N2822, ""),
                Frame(SearcherSession, A2806, "00000000000000010002"),
                Frame(MSession, A2806, "00000000000000010000"),
                Frame(XSession, A2806, "00000000000000010000"),
            ],
            Render(opened.Sends));
    }

 /// <summary>
 /// A full round: the opener <c>...00010000</c>; the member answers <c>04</c> and gets 2834 <c>0000</c> and
 /// 2806 <c>00000000000001010004</c>. Another member takes a dealer slot and the member is sent
 /// <c>00000000000001020004</c>. A refused answer from a third member ends the round with 2831
 /// <c>0200000000</c>.
 /// </summary>
    [DataFact]
    public void RetailRound_Accepted_ThenAnotherPick_ThenAnotherMembersRefusal()
    {
        var round = new DungeonFinderPartyRound();
        var m = Player(MSession);
        var x = Player(XSession);
        var y = Player(YSession);
        var opened = round.Open(Party, Five, SeatKeys.Of(Player(SearcherSession)), RoleKind.RoleDealer, [SeatKeys.Of(m), SeatKeys.Of(x), SeatKeys.Of(y)], Everyone);
        Assert.Equal([Frame(MSession, N2822, ""), Frame(MSession, A2806, "00000000000000010000")], To(MSession, opened.Sends));

        var mPick = round.SelectRole(SeatKeys.Of(m), 0x04, Everyone);
        Assert.Equal(SelectStatus.Accepted, mPick.Status);
        Assert.Equal(RoleKind.RoleHealer, mPick.Role);
        Assert.Equal([Frame(MSession, A2834, "0000"), Frame(MSession, A2806, "00000000000001010004")], To(MSession, mPick.Sends));

        var xPick = round.SelectRole(SeatKeys.Of(x), RoleKind.RoleDealer, Everyone);
        Assert.Equal(SelectStatus.Accepted, xPick.Status);
        Assert.Equal([Frame(MSession, A2806, "00000000000001020004")], To(MSession, xPick.Sends));
        Assert.Equal([Frame(XSession, A2834, "0000"), Frame(XSession, A2806, "00000000000001020002")], To(XSession, xPick.Sends));

        var yPick = round.SelectRole(SeatKeys.Of(y), RoleKind.RoleHealer, Everyone);
        Assert.Equal(SelectStatus.SlotTaken, yPick.Status);
        Assert.Equal(
            [
                Frame(YSession, A2834, "0800"),
                Frame(SearcherSession, N2831, "0200000000"),
                Frame(MSession, N2831, "0200000000"),
                Frame(XSession, N2831, "0200000000"),
                Frame(YSession, N2831, "0200000000"),
            ],
            Render(yPick.Sends));
    }

 /// <summary>
 /// A healer searches, another member takes the tank slot and a further member a dealer slot. The member
 /// answers <c>01</c> and gets 2834 <c>0800</c> and 2831 <c>0200000000</c>.
 /// </summary>
    [DataFact]
    public void RetailRound_TankSlotAlreadyTaken_IsRefused()
    {
        var round = new DungeonFinderPartyRound();
        var m = Player(MSession);
        var x = Player(XSession);
        var y = Player(YSession);
        round.Open(Party, Five, SeatKeys.Of(Player(SearcherSession)), RoleKind.RoleHealer, [SeatKeys.Of(x), SeatKeys.Of(y), SeatKeys.Of(m)], Everyone);

        Assert.Equal([Frame(MSession, A2806, "00000000000001000100")], To(MSession, round.SelectRole(SeatKeys.Of(x), RoleKind.RoleTank, Everyone).Sends));
        Assert.Equal([Frame(MSession, A2806, "00000000000001010100")], To(MSession, round.SelectRole(SeatKeys.Of(y), RoleKind.RoleDealer, Everyone).Sends));

        var mPick = round.SelectRole(SeatKeys.Of(m), 0x01, Everyone);

        Assert.Equal(SelectStatus.SlotTaken, mPick.Status);
        Assert.Equal([Frame(MSession, A2834, "0800"), Frame(MSession, N2831, "0200000000")], To(MSession, mPick.Sends));
        Assert.Equal([Frame(SearcherSession, N2831, "0200000000")], To(SearcherSession, mPick.Sends));
    }

 /// <summary>
 /// A healer searches and two members take dealer slots. The member answers <c>04</c> and gets 2834
 /// <c>0800</c> and 2831 <c>0200000000</c>.
 /// </summary>
    [DataFact]
    public void RetailRound_HealerSlotHeldBySearcher_IsRefused()
    {
        var round = new DungeonFinderPartyRound();
        var m = Player(MSession);
        var x = Player(XSession);
        var y = Player(YSession);
        var opened = round.Open(Party, Five, SeatKeys.Of(Player(SearcherSession)), RoleKind.RoleHealer, [SeatKeys.Of(m), SeatKeys.Of(x), SeatKeys.Of(y)], Everyone);
        Assert.Equal([Frame(MSession, N2822, ""), Frame(MSession, A2806, "00000000000001000000")], To(MSession, opened.Sends));

        Assert.Equal([Frame(MSession, A2806, "00000000000001010000")], To(MSession, round.SelectRole(SeatKeys.Of(x), RoleKind.RoleDealer, Everyone).Sends));
        Assert.Equal([Frame(MSession, A2806, "00000000000001020000")], To(MSession, round.SelectRole(SeatKeys.Of(y), RoleKind.RoleDealer, Everyone).Sends));
        var mPick = round.SelectRole(SeatKeys.Of(m), 0x04, Everyone);

        Assert.Equal([Frame(MSession, A2834, "0800"), Frame(MSession, N2831, "0200000000")], To(MSession, mPick.Sends));
    }

    [DataFact]
    public void EveryMemberHoldingARole_ClosesTheRound()
    {
        var round = new DungeonFinderPartyRound();
        var m = Player(MSession);
        var x = Player(XSession);
        round.Open(Party, Five, SeatKeys.Of(Player(SearcherSession)), RoleKind.RoleDealer, [SeatKeys.Of(m), SeatKeys.Of(x)], Everyone);
        Assert.Equal(SelectStatus.Accepted, round.SelectRole(SeatKeys.Of(m), RoleKind.RoleTank, Everyone).Status);

        var last = round.SelectRole(SeatKeys.Of(x), RoleKind.RoleHealer, Everyone);

        Assert.Equal(SelectStatus.Completed, last.Status);
        Assert.Equal(
            [
                Frame(XSession, A2834, "0000"),
                Frame(SearcherSession, A2806, "00000000000001010102"),
                Frame(MSession, A2806, "00000000000001010101"),
                Frame(XSession, A2806, "00000000000001010104"),
            ],
            Render(last.Sends));
        
        Assert.Equal(OpenStatus.Opened,
            round.Open(Party, Five, SeatKeys.Of(Player(SearcherSession)), RoleKind.RoleDealer, [SeatKeys.Of(m), SeatKeys.Of(x)], Everyone).Status);
    }

    [DataFact]
    public void MultiRoleFlags_TakeTheFirstFreeRole_InTankHealerDealerOrder()
    {
        var round = new DungeonFinderPartyRound();
        var m = Player(MSession);
        var x = Player(XSession);
        round.Open(Party, Five, SeatKeys.Of(Player(SearcherSession)), RoleKind.RoleTank, [SeatKeys.Of(m), SeatKeys.Of(x)], Everyone);

        var pick = round.SelectRole(SeatKeys.Of(m), RoleKind.RoleTank | RoleKind.RoleHealer, Everyone);

        Assert.Equal(RoleKind.RoleHealer, pick.Role);
    }

 /// <summary>A 1568 from a window whose round no longer exists is refused to the sender alone.</summary>
    [DataFact]
    public void SelectRole_WithNoOpenRound_ClosesTheSendersWindowOnly()
    {
        var round = new DungeonFinderPartyRound();

        var pick = round.SelectRole(SeatKeys.Of(Player(MSession)), 0x04, Everyone);

        Assert.Equal(SelectStatus.NoRound, pick.Status);
        Assert.Equal([Frame(MSession, A2834, "0800"), Frame(MSession, N2831, "0200000000")], Render(pick.Sends));
    }

    [DataFact]
    public void Cancel_EndsTheRoundForEveryMember_TheCancellerIncluded()
    {
        var round = new DungeonFinderPartyRound();
        var m = Player(MSession);
        var x = Player(XSession);
        round.Open(Party, Five, SeatKeys.Of(Player(SearcherSession)), RoleKind.RoleDealer, [SeatKeys.Of(m), SeatKeys.Of(x)], Everyone);

        var cancel = round.Cancel(SeatKeys.Of(x), Everyone);

        Assert.True(cancel.Ended);
        Assert.Equal(
            [
                Frame(SearcherSession, N2831, "0200000000"),
                Frame(MSession, N2831, "0200000000"),
                Frame(XSession, N2831, "0200000000"),
            ],
            Render(cancel.Sends));
        Assert.Equal(SelectStatus.NoRound, round.SelectRole(SeatKeys.Of(m), 0x04, Everyone).Status);
    }

    [DataFact]
    public void Cancel_WithNoOpenRound_SendsNothing()
    {
        var cancel = new DungeonFinderPartyRound().Cancel(SeatKeys.Of(Player(MSession)), Everyone);

        Assert.False(cancel.Ended);
        Assert.Empty(cancel.Sends);
    }

 /// <summary>
 /// The 1558 route: while searching, the round's 2806 places every member in the round, and the finder tab's
 /// Find button sends 1558 instead. The searcher can also send it, and its cancel ends the round for everyone.
 /// </summary>
    [DataFact]
    public void Cancel_BySearcherThroughItsFinderTab_EndsTheRoundForEveryMember()
    {
        var round = new DungeonFinderPartyRound();
        var searcher = Player(SearcherSession);
        var m = Player(MSession);
        round.Open(Party, Five, SeatKeys.Of(searcher), RoleKind.RoleDealer, [SeatKeys.Of(m), SeatKeys.Of(Player(XSession))], Everyone);
        round.SelectRole(SeatKeys.Of(m), RoleKind.RoleHealer, Everyone);

        var cancel = round.Cancel(SeatKeys.Of(searcher), Everyone);

        Assert.True(cancel.Ended);
        Assert.Equal(
            [
                Frame(SearcherSession, N2831, "0200000000"),
                Frame(MSession, N2831, "0200000000"),
                Frame(XSession, N2831, "0200000000"),
            ],
            Render(cancel.Sends));
        Assert.Equal(OpenStatus.Opened,
            round.Open(Party, Five, SeatKeys.Of(searcher), RoleKind.RoleDealer, [SeatKeys.Of(m)], Everyone).Status);
    }

 /// <summary>
 /// The finder window hides itself once its frame timer passes 40.0 seconds and sends nothing, so the round
 /// can only give up after that point.
 /// </summary>
    [DataFact]
    public void Timeout_IsNoEarlierThanTheClientsFortySecondWindow()
    {
        Assert.Equal(TimeSpan.FromSeconds(40), DungeonFinderPartyRound.ClientRoleCheckWindow);
        Assert.Equal(40.0f, BitConverter.ToSingle(Convert.FromHexString("00002042")));
        Assert.True(DungeonFinderPartyRound.SelectRoleTimeout >= DungeonFinderPartyRound.ClientRoleCheckWindow);
    }

 /// <summary>
 /// The member answers and the other member's window runs out: the timeout ends the round with 2831 sent to
 /// everyone, including the answered member and the searcher, whose finder UI only that 2831 resets. The
 /// party can search again.
 /// </summary>
    [DataFact]
    public void Expire_EndsARoundWithARoleUnpicked_ForEveryMember()
    {
        var round = new DungeonFinderPartyRound();
        var searcher = Player(SearcherSession);
        var m = Player(MSession);
        var x = Player(XSession);
        var opened = round.Open(Party, Five, SeatKeys.Of(searcher), RoleKind.RoleDealer, [SeatKeys.Of(m), SeatKeys.Of(x)], Everyone);
        Assert.Equal(SelectStatus.Accepted, round.SelectRole(SeatKeys.Of(m), RoleKind.RoleHealer, Everyone).Status);

        var expired = round.Expire(opened.RoundId, Everyone);

        Assert.True(expired.Ended);
        Assert.Equal(
            [
                Frame(SearcherSession, N2831, "0200000000"),
                Frame(MSession, N2831, "0200000000"),
                Frame(XSession, N2831, "0200000000"),
            ],
            Render(expired.Sends));
        Assert.Equal(SelectStatus.NoRound, round.SelectRole(SeatKeys.Of(x), 0x04, Everyone).Status);
        Assert.Equal(OpenStatus.Opened,
            round.Open(Party, Five, SeatKeys.Of(searcher), RoleKind.RoleDealer, [SeatKeys.Of(m), SeatKeys.Of(x)], Everyone).Status);
    }

 /// <summary>A round that completed before its timeout: the timeout finds nothing to end.</summary>
    [DataFact]
    public void Expire_AfterTheRoundCompleted_SendsNothing()
    {
        var round = new DungeonFinderPartyRound();
        var m = Player(MSession);
        var opened = round.Open(Party, Five, SeatKeys.Of(Player(SearcherSession)), RoleKind.RoleDealer, [SeatKeys.Of(m)], Everyone);
        Assert.Equal(SelectStatus.Completed, round.SelectRole(SeatKeys.Of(m), RoleKind.RoleTank, Everyone).Status);

        var expired = round.Expire(opened.RoundId, Everyone);

        Assert.False(expired.Ended);
        Assert.Empty(expired.Sends);
    }

 /// <summary>
 /// A first round is cancelled and the party searches again within 41 seconds. The first round's timeout
 /// must not end the second round, which keeps its own timeout.
 /// </summary>
    [DataFact]
    public void Expire_OfAnEarlierRound_LeavesThePartysNextRoundOpen()
    {
        var round = new DungeonFinderPartyRound();
        var searcher = Player(SearcherSession);
        var m = Player(MSession);
        var first = round.Open(Party, Five, SeatKeys.Of(searcher), RoleKind.RoleDealer, [SeatKeys.Of(m)], Everyone);
        Assert.True(round.Cancel(SeatKeys.Of(m), Everyone).Ended);
        var second = round.Open(Party, Five, SeatKeys.Of(searcher), RoleKind.RoleDealer, [SeatKeys.Of(m)], Everyone);
        Assert.NotEqual(first.RoundId, second.RoundId);

        var stale = round.Expire(first.RoundId, Everyone);

        Assert.False(stale.Ended);
        Assert.Empty(stale.Sends);
        Assert.Equal(OpenStatus.AlreadyOpen,
            round.Open(Party, Five, SeatKeys.Of(searcher), RoleKind.RoleDealer, [SeatKeys.Of(m)], Everyone).Status);
        Assert.True(round.Expire(second.RoundId, Everyone).Ended);
    }

 /// <summary>
 /// A member goes offline before the timeout: the round is dropped as any stale round is, and only the
 /// members still present receive the 2831, once.
 /// </summary>
    [DataFact]
    public void Expire_OfARoundThatLostAMember_TellsTheMembersStillThereOnce()
    {
        var round = new DungeonFinderPartyRound();
        var opened = round.Open(Party, Five, SeatKeys.Of(Player(SearcherSession)), RoleKind.RoleDealer,
            [SeatKeys.Of(Player(MSession)), SeatKeys.Of(Player(XSession))], Everyone);
        DungeonFinderPartyRound.SeatCheck xGone = (_, seat) => seat.SessionId != XSession;

        var expired = round.Expire(opened.RoundId, xGone);

        Assert.True(expired.Ended);
        Assert.Equal(
            [
                Frame(SearcherSession, N2831, "0200000000"),
                Frame(MSession, N2831, "0200000000"),
            ],
            Render(expired.Sends));
    }

 /// <summary>
 /// A member leaves the world before answering: the round can never complete, so the next access drops it
 /// and notifies the members still present. The member's own 1568 then finds no round and gets only the 2834.
 /// </summary>
    [DataFact]
    public void RoundWithAMemberGone_IsDroppedOnTheNextTouch()
    {
        var round = new DungeonFinderPartyRound();
        var m = Player(MSession);
        var x = Player(XSession);
        round.Open(Party, Five, SeatKeys.Of(Player(SearcherSession)), RoleKind.RoleDealer, [SeatKeys.Of(m), SeatKeys.Of(x)], Everyone);
        DungeonFinderPartyRound.SeatCheck xGone = (_, seat) => seat.SessionId != XSession;

        var pick = round.SelectRole(SeatKeys.Of(m), 0x04, xGone);

        Assert.Equal(SelectStatus.NoRound, pick.Status);
        Assert.Equal(
            [
                Frame(SearcherSession, N2831, "0200000000"),
                Frame(MSession, N2831, "0200000000"),
                Frame(MSession, A2834, "0800"),
            ],
            Render(pick.Sends));
    }

    [DataFact]
    public void Open_WhileThePartysRoundIsLive_OpensNothing()
    {
        var round = new DungeonFinderPartyRound();
        var m = Player(MSession);
        round.Open(Party, Five, SeatKeys.Of(Player(SearcherSession)), RoleKind.RoleDealer, [SeatKeys.Of(m)], Everyone);

        var again = round.Open(Party, Five, SeatKeys.Of(m), RoleKind.RoleHealer, [SeatKeys.Of(Player(SearcherSession))], Everyone);

        Assert.Equal(OpenStatus.AlreadyOpen, again.Status);
        Assert.Empty(again.Sends);
    }

    [DataFact]
    public void Open_WithNoSlotForTheSearchersRole_OpensNothing()
    {
        var round = new DungeonFinderPartyRound();
        var m = Player(MSession);
        var noTank = Five with { Role_Tanker = 0 };

        var opened = round.Open(Party, noTank, SeatKeys.Of(Player(SearcherSession)), RoleKind.RoleTank, [SeatKeys.Of(m)], Everyone);

        Assert.Equal(OpenStatus.SearcherHasNoSlot, opened.Status);
        Assert.Empty(opened.Sends);
        Assert.Equal(SelectStatus.NoRound, round.SelectRole(SeatKeys.Of(m), 0x04, Everyone).Status);
    }
}
