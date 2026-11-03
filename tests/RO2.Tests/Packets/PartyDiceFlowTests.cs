using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
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

        public static async Task<Round> OpenAsync()
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
            var death = npcs.GetNpc(handle)!.DeathTime;
            GroundItemService.BeginCorpse(map, handle, CorpseShareRig.Spawn.AssetIndex, death, killer.CharacterId,
                killer.CharacterUid, sharedLooters: [new GroundItemService.CorpseLooter(member.CharacterId, MemberGid)]);
            var item = GroundItemService.AddCorpseItem(map, handle, RollItem, 1, diceBound: true)!;
            var round = new Round { Rig = rig, Killer = killer, Member = member, Item = item, Map = map, Handle = handle, Death = death };
            Assert.True(GroundItemService.TryGetCorpseLoot(map, handle, killer.CharacterId, death, out var loot));
            var party = rig.Party.GetPlayerParty(killer.SessionId)!;
            await PartyDiceService.OpenAsync(PartyDiceService.Deps.From(rig.Services, NullLogger.Instance), killer, party, loot, item);
            return round;
        }

        public Task AnswerAsync(PlayerState who, byte choice)
            => PartyDiceService.AnswerAsync(PartyDiceService.Deps.From(Rig.Services, NullLogger.Instance), who, Item.Uid, choice);

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
        Assert.True(PartyDiceService.IsRolling(r.Item.Uid));
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
        Assert.True(PartyDiceService.IsRolling(r.Item.Uid));

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

        Assert.False(PartyDiceService.IsRolling(r.Item.Uid));
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
        Assert.False(PartyDiceService.IsRolling(r.Item.Uid));
        Assert.True(GroundItemService.TryGetCorpseLoot(r.Map, r.Handle, r.Member.CharacterId, r.Death, out var left));
        Assert.Equal(new[] { r.Item }, left.Items);
        Assert.True(left.Items[0].DiceBound);
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
