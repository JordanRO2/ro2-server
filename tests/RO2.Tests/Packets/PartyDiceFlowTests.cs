using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Parties.Application;
using RO2.PartyDice.Domain;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// The party loot dice flow over real sessions: who is told what (2468/2469/2470), which answers are ignored, and what the
/// settle leaves on the corpse (the dice mark released after an all-pass, kept when the winner's award does not land).
/// </summary>
[Collection(CorpseShareRigCollection.Name)]
public sealed class PartyDiceFlowTests
{
    private const uint MemberHandle = 0x0852F111u, KillerHandle = 0x0852F511u;
    private const ulong MemberGid = 0x0DE0B6B3A7E8421CUL;
    private const uint RollItem = 22310852;

    private static string ToHex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    /// <summary>A killer and a member in one mode-2 party, with a dice-bound item on the killer's corpse.</summary>
    private sealed class Round : IAsyncDisposable
    {
        public required CorpseShareRig Rig;
        public required PlayerState Killer, Member;
        public required GroundItemService.CorpseItem Item;
        public required uint Map, Handle;
        public required DateTime? Death;
        public required PartyDiceService Dice;

        public static async Task<Round> OpenAsync(TimeSpan? window = null, bool breakMemberSend = false)
        {
            var rig = await CorpseShareRig.StartAsync(2);
            var (npcs, handle) = CorpseShareRig.OneDeadNpc(out var map);
            var killer = rig.AddPlayer(0, charId: 81, map, handle: KillerHandle);
            var member = rig.AddPlayer(1, charId: 82, map, gid: MemberGid, handle: MemberHandle);
            rig.Party.Track(new PartyState
            {
                PartyId = 9019, LeaderSessionId = killer.SessionId, PartyType = 2, ItemRule = 2, ItemQuality = 4,
                MemberSessionIds = [killer.SessionId, member.SessionId],
            });
            rig.Loot(npcs);
            var dice = window is { } w
                ? new PartyDiceService(rig.Party, rig.Services.GetRequiredService<IPartyRoster>(), rig.Players, rig.Broadcast, npcs,
                    rig.Data, rig.ScopeFactory, NullLogger<PartyDiceService>.Instance, w)
                : rig.Dice;
            var death = npcs.GetNpc(handle)!.DeathTime;
            GroundItemService.BeginCorpse(map, handle, CorpseShareRig.Spawn.AssetIndex, death, killer.CharacterId,
                killer.CharacterUid, sharedLooters: [new GroundItemService.CorpseLooter(member.CharacterId, MemberGid)]);
            var item = GroundItemService.AddCorpseItem(map, handle, RollItem, 1, diceBound: true)!;
            var round = new Round { Rig = rig, Killer = killer, Member = member, Item = item, Map = map, Handle = handle, Death = death, Dice = dice };
            Assert.True(GroundItemService.TryGetCorpseLoot(map, handle, killer.CharacterId, death, out var loot));
            if (breakMemberSend) // a disposed send lock makes every send to the member throw
                ((SemaphoreSlim)typeof(ProudSession).GetField("_sendLock",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(rig.Sessions[1])!)
                    .Dispose();
            await dice.OpenAsync(killer, rig.Party.GetPlayerParty(killer.SessionId)!.PartyId, loot, item);
            return round;
        }

        public Task AnswerAsync(PlayerState who, byte choice)
            => Dice.AnswerAsync(who, Item.Uid, choice);

        /// <summary>What each of the two clients received since the last call.</summary>
        public async Task<(List<string> Killer, List<string> Member)> FramesAsync()
        {
            await Rig.SendMarkersAsync();
            return (await Rig.FramesUntilMarkerAsync(0), await Rig.FramesUntilMarkerAsync(1));
        }

        public ValueTask DisposeAsync()
        {
            GroundItemService.RemoveCorpse(Map, Handle);
            return Rig.DisposeAsync();
        }
    }

    [Fact]
    public async Task OpeningARoll_Tells2468ToEveryParticipant_AndHoldsTheItem()
    {
        await using var r = await Round.OpenAsync();
        var (killer, member) = await r.FramesAsync();
        var open = $"2468:{ToHex(PartyDicePackets.BuildRouletteOpen2468(r.Item.Uid, RollItem, 1))}";
        Assert.Equal(new[] { open }, killer);
        Assert.Equal(new[] { open }, member);
        Assert.True(r.Dice.IsRolling(r.Item.Uid));
    }

    [Fact]
    public async Task UnknownChoice_IsIgnored_ThenOneAnswerPerParticipant_ThenTheAllPassNoticeReleasesTheMark()
    {
        await using var r = await Round.OpenAsync();
        await r.FramesAsync();

        await r.AnswerAsync(r.Killer, 7);
        var (k0, m0) = await r.FramesAsync();
        Assert.Empty(k0);
        Assert.Empty(m0);
        Assert.True(r.Dice.IsRolling(r.Item.Uid));

        await r.AnswerAsync(r.Killer, PartyDiceRolls.ChoicePass);
        var killerGid = PartyAnswerPackets.MemberGid(r.Killer);
        var passed = $"2470:{ToHex(PartyDicePackets.BuildRouletteRefuse2470(killerGid, RollItem, r.Item.Uid))}";
        var (k1, m1) = await r.FramesAsync();
        Assert.Equal(new[] { passed }, k1);
        Assert.Equal(new[] { passed }, m1);

        await r.AnswerAsync(r.Killer, PartyDiceRolls.ChoiceHigh); // a second answer from the same participant
        var (k2, m2) = await r.FramesAsync();
        Assert.Empty(k2);
        Assert.Empty(m2);

        await r.AnswerAsync(r.Member, PartyDiceRolls.ChoicePass);
        var memberPassed = $"2470:{ToHex(PartyDicePackets.BuildRouletteRefuse2470(MemberGid, RollItem, r.Item.Uid))}";
        var allPassed = $"2469:{ToHex(PartyDicePackets.BuildRouletteResult2469(PartyDicePackets.ResultAllPassed, 0, 0, RollItem, r.Item.Uid))}";
        var (k3, m3) = await r.FramesAsync();
        Assert.Equal(new[] { memberPassed, allPassed }, k3);
        Assert.Equal(new[] { memberPassed, allPassed }, m3);

        Assert.False(r.Dice.IsRolling(r.Item.Uid));
        Assert.True(GroundItemService.TryGetCorpseLoot(r.Map, r.Handle, r.Member.CharacterId, r.Death, out var left));
        Assert.Equal(new[] { r.Item with { DiceBound = false } }, left.Items);
    }

    [Fact]
    public async Task RollsAreBroadcastInAnswerOrder_AndAWinnerWhoseAwardDoesNotLandLeavesTheItemDiceBound()
    {
        await using var r = await Round.OpenAsync();
        await r.FramesAsync();

        await r.AnswerAsync(r.Killer, PartyDiceRolls.ChoiceLow);
        await r.AnswerAsync(r.Member, PartyDiceRolls.ChoiceHigh);
        var (killer, member) = await r.FramesAsync();
        Assert.Equal(killer, member);
        Assert.Equal(2, killer.Count);

        var killerRoll = RollOf(killer[0], PartyAnswerPackets.MemberGid(r.Killer), r.Item);
        var memberRoll = RollOf(killer[1], MemberGid, r.Item);
        Assert.InRange(killerRoll, (ushort)1, (ushort)1000);
        Assert.InRange(memberRoll, (ushort)1001, (ushort)2000);

        // The rig has no inventory, so the member's win cannot land: the award fails and the item stays on the corpse, still marked.
        Assert.False(r.Dice.IsRolling(r.Item.Uid));
        Assert.True(GroundItemService.TryGetCorpseLoot(r.Map, r.Handle, r.Member.CharacterId, r.Death, out var left));
        Assert.Equal(new[] { r.Item }, left.Items);
        Assert.True(left.Items[0].DiceBound);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition());
    }

    /// <summary>Audit [22]: a 2468 send that throws must not strand the roll or the corpse hold; the window still settles it.</summary>
    [Fact]
    public async Task AFailedOpenSend_StillArmsTheTimer_SoTheRollAndTheCorpseHoldAreReleased()
    {
        await using var r = await Round.OpenAsync(TimeSpan.FromMilliseconds(150), breakMemberSend: true);
        await WaitUntilAsync(() => !r.Dice.IsRolling(r.Item.Uid)); // the timer settled it; TrySettleAsync's finally releases the hold
        // The all-pass broadcast to the broken member throws; the dice-bind must be cleared regardless.
        await WaitUntilAsync(() => GroundItemService.TryGetCorpseLoot(r.Map, r.Handle, r.Member.CharacterId, r.Death, out var l) && !l.Items[0].DiceBound);
    }

    [Fact]
    public async Task TimeoutWithNoAnswers_SettlesAnAllPass_AndReleasesTheMark()
    {
        await using var r = await Round.OpenAsync(TimeSpan.FromMilliseconds(150));
        await r.FramesAsync();
        await WaitUntilAsync(() => !r.Dice.IsRolling(r.Item.Uid));
        await WaitUntilAsync(() => GroundItemService.TryGetCorpseLoot(r.Map, r.Handle, r.Member.CharacterId, r.Death, out var l) && !l.Items[0].DiceBound);
        var allPassed = $"2469:{ToHex(PartyDicePackets.BuildRouletteResult2469(PartyDicePackets.ResultAllPassed, 0, 0, RollItem, r.Item.Uid))}";
        var (killer, member) = await r.FramesAsync();
        Assert.Equal(new[] { allPassed }, killer);
        Assert.Equal(new[] { allPassed }, member);
    }

    [Fact]
    public async Task TimeoutWithOneRoll_SettlesTheWinnerWithoutWaitingForTheOther()
    {
        await using var r = await Round.OpenAsync(TimeSpan.FromMilliseconds(300));
        await r.FramesAsync();
        await r.AnswerAsync(r.Killer, PartyDiceRolls.ChoiceHigh);
        Assert.True(r.Dice.IsRolling(r.Item.Uid));
        await WaitUntilAsync(() => !r.Dice.IsRolling(r.Item.Uid));
        await Task.Delay(300); // the award attempt (it cannot land here) and the corpse release follow the settle
        var (killer, member) = await r.FramesAsync();
        Assert.Equal(killer, member);
        Assert.Single(killer);
        Assert.StartsWith("2469:", killer[0]); // the killer's roll, never an all-pass notice
        Assert.NotEqual(0, BinaryPrimitives.ReadUInt16LittleEndian(Convert.FromHexString(killer[0]["2469:".Length..]).AsSpan(12)));
        Assert.True(GroundItemService.TryGetCorpseLoot(r.Map, r.Handle, r.Member.CharacterId, r.Death, out var left));
        Assert.True(left.Items[0].DiceBound);
    }

    [Fact]
    public async Task SettlingByAnswers_LeavesTheTimerNothingToDo()
    {
        await using var r = await Round.OpenAsync(TimeSpan.FromMilliseconds(300));
        await r.FramesAsync();
        await r.AnswerAsync(r.Killer, PartyDiceRolls.ChoicePass);
        await r.AnswerAsync(r.Member, PartyDiceRolls.ChoicePass);
        await Task.Delay(700); // past the window: the timer must not settle (and announce) a second time
        var (killer, member) = await r.FramesAsync();
        Assert.Equal(3, killer.Count); // two passes and one all-pass notice
        Assert.Equal(killer, member);
        Assert.Equal(1, killer.Count(f => f.StartsWith("2469:")));
    }

    [Fact]
    public void Translator_ReproducesTheDiceFramesFromTheEffects()
    {
        var item = new GroundItemService.CorpseItem(0x0E043DA80D8F552FUL, 22300102, 1);
        Assert.Equal((ZoneS2COpcodes.OnLootingBoxRouletteOpen, "0115150090e3000000002f558f0da83d040ec645540101"),
            Frame(new Opened([1u, 2u]), item));
        Assert.Equal((ZoneS2COpcodes.OnLootingBoxRouletteResult, "0120200092e3000000000000860681d9e7a7b3b6e00dc64554012f558f0da83d040e"),
            Frame(new Answered(0x0DE0B6B3A7E7D981, 1670), item));
        Assert.Equal((ZoneS2COpcodes.OnLootingBoxRouletteRefuse, "011c1c0093e3000000004cab56ab8f3c296fd06e54010d278a0da83d040e"),
            Frame(new Refused(0x6F293C8FAB56AB4C), new GroundItemService.CorpseItem(0x0E043DA80D8A270D, 22310608, 1)));
        Assert.Equal((ZoneS2COpcodes.OnLootingBoxRouletteResult, "0120200092e300000000733d000000000000000000002ddbfe00d9f24f0ea83d040e"),
            Frame(new AllPassed(), new GroundItemService.CorpseItem(0x0E043DA80E4FF2D9, 16702253, 1)));
        Assert.Null(PartyDiceFrames.ToFrame(new Winner(1, 2, 3), item));
        Assert.Null(PartyDiceFrames.ToFrame(new HoldDiceBound(), item));
        Assert.Null(PartyDiceFrames.ToFrame(new ReleaseDiceBound(), item));

        static (ushort, string) Frame(DiceEffect e, GroundItemService.CorpseItem i)
        {
            var (op, body) = PartyDiceFrames.ToFrame(e, i)!.Value;
            return (op, ToHex(body));
        }
    }

    /// <summary>Checks that <paramref name="frame"/> is the 2469 of a roll by <paramref name="gid"/> and returns the roll.</summary>
    private static ushort RollOf(string frame, ulong gid, GroundItemService.CorpseItem item)
    {
        Assert.StartsWith("2469:", frame);
        var body = Convert.FromHexString(frame["2469:".Length..]);
        var roll = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(12));
        Assert.Equal(frame, $"2469:{ToHex(PartyDicePackets.BuildRouletteResult2469(0, roll, gid, RollItem, item.Uid))}");
        return roll;
    }
}

/// <summary>The pure dice rules beyond the corpse-loot tests: the seeded roll stream, unknown choices, and winner ties.</summary>
public sealed class PartyDiceRulesTests
{
    [Fact]
    public void TryRoll_DrawsTheSeededStream_OneValueForARollAndNoneForAPassOrUnknownChoice()
    {
        var rng = new Random(77);
        var mirror = new Random(77);
        Assert.True(PartyDiceRolls.TryRoll(PartyDiceRolls.ChoiceHigh, rng, out var high));
        Assert.Equal((ushort)mirror.Next(1001, 2001), high);
        Assert.False(PartyDiceRolls.TryRoll(PartyDiceRolls.ChoicePass, rng, out var pass));
        Assert.False(PartyDiceRolls.TryRoll(3, rng, out var unknown));
        Assert.False(PartyDiceRolls.TryRoll(255, rng, out var unknown2));
        Assert.Equal((0, 0, 0), (pass, unknown, unknown2));
        Assert.True(PartyDiceRolls.TryRoll(PartyDiceRolls.ChoiceLow, rng, out var low));
        Assert.Equal((ushort)mirror.Next(1, 1001), low);
    }

    [Fact]
    public void PickWinner_BreaksATieByTheEarliestAnswerOrder_NotByListPosition()
    {
        Assert.Equal(1, PartyDiceRolls.PickWinner(new (ushort, long)[] { (1500, 5), (1500, 2), (1500, 9) }));
        Assert.Equal(2, PartyDiceRolls.PickWinner(new (ushort, long)[] { (900, 0), (1500, 4), (1500, 3) }));
        Assert.Equal(0, PartyDiceRolls.PickWinner(new (ushort, long)[] { (7, 0) }));
    }
}
