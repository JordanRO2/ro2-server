using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Network.Protocol;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Party loot mode 2 (Roll Dice) and PC kills: there is no turn. An NPC kill under mode 2 is handed to one member at a time,
/// but a PC kill is shared with the members. Every kill this server makes is a PC's, so every mode-2 kill is shared with the
/// members on the killer's map, and any member may open the roll of a dice item on it.
/// The reference frames are identified by session and time. The CorpseShareRig tests share their corpse key with SharedCorpseLootTests;
/// see CorpseShareRigCollection for why the two classes run on their own.
/// </summary>
[Collection(CorpseShareRigCollection.Name)]
public sealed class ModeTwoTurnTests
{
    private static string ToHex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    
 /// <summary>A raid PC kill: the corpse and the killer .</summary>
    private const uint RaidPcKillCorpse = 0x06EB2313u, RaidPcKiller = 0x0D58F111u;

 /// <summary>
 /// The grant as seen by a member other than the killer, followed by the 2392 naming the killer.
 /// </summary>
    private const string RaidMemberGrant = "010f0f00dee30000000001001323eb0601";
    private const string RaidDeadNotify = "01141400cbe2000000001323eb0611f1580d7d260100";

    [Fact]
    public void RaidPcKill_TheMembersGrantAndThe2392_AreRetailsFrames()
    {
        Assert.Equal(RaidMemberGrant, ToHex(CombatPackets.BuildLootingAuthority2471(RaidPcKillCorpse)));
        
        Assert.Equal(RaidDeadNotify, ToHex(ActorVitalsPackets.BuildDeadNotify2392(RaidPcKillCorpse, RaidPcKiller, 0x0001267D)));
    }

    
    private const uint MemberHandle = 0x0852F111u, KillerHandle = 0x0852F511u, RollCorpse = 0x3C5FB713u;
    private const ulong MemberGid = 0x0DE0B6B3A7E8421CUL, RollUid = 0x0E043DA80DAD0C41UL;
    private const uint RollItem = 22310852; 

 /// <summary>The member's grant for a corpse the member killed, followed by its 2392.</summary>
    private const string MemberGrant = "010f0f00dee300000000010013b75f3c01";
    private const string MemberDeadNotify = "01141400cbe20000000013b75f3c11f552081dd00000"; 
 /// <summary>The answer to the member's 1338 request.</summary>
    private const string Member2374 =
        "01363600aee20000000013b75f3c040101c46f5401410cad0da83d040e010000000000000000000000000000000000011c42e8a7b3b6e00d";
    private const string Member1322 = "01151500a3e211f15208410cad0da83d040e0013b75f3c"; 
    private const string RollOpen2468 = "0115150090e300000000410cad0da83d040ec46f540101";   

    [Fact]
    public void MemberOpensARollOnAnotherMembersKill_TheFramesAreRetails()
    {
        Assert.Equal(MemberGrant, ToHex(CombatPackets.BuildLootingAuthority2471(RollCorpse)));
        Assert.Equal(MemberDeadNotify, ToHex(ActorVitalsPackets.BuildDeadNotify2392(RollCorpse, KillerHandle, 0xD01D)));

        Assert.True(ItemPickUpHandler.TryParse(Convert.FromHexString(Member1322), out var uid, out var type, out var corpse));
        Assert.Equal((RollUid, (byte)0, RollCorpse), (uid, type, corpse));
        Assert.Equal(RollOpen2468, ToHex(PartyDicePackets.BuildRouletteOpen2468(RollUid, RollItem, 1)));

        
        
        
        var built = PickupNotifyPackets.BuildHeartBeatItem2374(RollCorpse,
            [new GroundItemService.CorpseItem(RollUid, RollItem, 1)], [MemberGid]);
        var retail = Convert.FromHexString(Member2374);
        Assert.Equal((byte)4, retail[14]);
        Assert.Equal((byte)0, built[14]);
        retail[14] = 0;
        Assert.Equal(ToHex(retail), ToHex(built));
    }

    
 /// <summary>
 /// Kill after kill, in a normal party and in a raid, the corpse goes to the same members: every one online on the killer's map, whoever killed.
 /// A rotating turn, one member per corpse, is the NPC-kill rule and does not apply to PC kills.
 /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task ModeTwo_EveryPcKillIsSharedWithTheMembersOnTheMap_NoTurn(int partyType)
    {
        await using var rig = await CorpseShareRig.StartAsync(4);
        var (npcs, handle) = CorpseShareRig.OneDeadNpc(out var map);
        var a = rig.AddPlayer(0, charId: 61, map);
        var b = rig.AddPlayer(1, charId: 62, map);
        var away = rig.AddPlayer(2, charId: 63, map + 1);
        var c = rig.AddPlayer(3, charId: 64, map);
        var party = new PartyState
        {
            PartyId = 9002, LeaderSessionId = a.SessionId, PartyType = (byte)partyType, ItemRule = 2, ItemQuality = 4,
            MemberSessionIds = [a.SessionId, b.SessionId, away.SessionId, c.SessionId],
        };
        rig.Party.Track(party);
        var loot = rig.Loot(npcs);

        for (int kill = 0; kill < 3; kill++)
            Assert.Equal(new[] { b, c }, loot.SharedCorpseLooters(a, handle));
        Assert.Equal(new[] { a, c }, loot.SharedCorpseLooters(b, handle));
        Assert.Equal(new[] { a, b }, loot.SharedCorpseLooters(c, handle));

        away.MapId = map; 
        Assert.Equal(new[] { b, away, c }, loot.SharedCorpseLooters(a, handle));
    }

 /// <summary>
 /// The member's side of a mode-2 kill by a party mate: the member is granted the corpse before the 2392, and the member's 1322 request
 /// for a dice item opens the roll for both. Both passing leaves the item on the corpse, no longer rolled.
 /// </summary>
    [Fact]
    public async Task ModeTwo_AMembersPickupOfADiceItemOnAMatesKill_OpensTheRoll()
    {
        await using var rig = await CorpseShareRig.StartAsync(2);
        var (npcs, handle) = CorpseShareRig.OneDeadNpc(out var map);
        var killer = rig.AddPlayer(0, charId: 71, map, handle: KillerHandle);
        var member = rig.AddPlayer(1, charId: 72, map, gid: MemberGid, handle: MemberHandle);
        var party = new PartyState
        {
            PartyId = 9003, LeaderSessionId = killer.SessionId, PartyType = 2, ItemRule = 2, ItemQuality = 4,
            MemberSessionIds = [killer.SessionId, member.SessionId],
        };
        rig.Party.Track(party);
        var loot = rig.Loot(npcs);

        var sharers = loot.SharedCorpseLooters(killer, handle);
        Assert.Equal(new[] { member }, sharers);
        var death = npcs.GetNpc(handle)!.DeathTime;
        GroundItemService.BeginCorpse(map, handle, CorpseShareRig.Spawn.AssetIndex, death, killer.CharacterId,
            killer.CharacterUid, sharedLooters: [new GroundItemService.CorpseLooter(member.CharacterId, MemberGid)]);
        var item = GroundItemService.AddCorpseItem(map, handle, RollItem, 1, diceBound: true)!;
        try
        {
            var request = Convert.FromHexString(Member1322);
            BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(2 + 8), item.Uid); 
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(2 + 17), handle);  
            var handler = new ItemPickUpHandler(rig.ScopeFactory, NullLogger<ItemPickUpHandler>.Instance, rig.Players,
                rig.Broadcast);
            await handler.HandleAsync(rig.Sessions[1], request);
            await rig.SendMarkersAsync();

            var open = $"2468:{ToHex(PartyDicePackets.BuildRouletteOpen2468(item.Uid, RollItem, 1))}";
            Assert.Equal(new[] { open }, await rig.FramesUntilMarkerAsync(0));
            Assert.Equal(new[] { open }, await rig.FramesUntilMarkerAsync(1));
            Assert.True(PartyDiceService.IsRolling(item.Uid));

            var deps = PartyDiceService.Deps.From(rig.Services, NullLogger.Instance);
            await PartyDiceService.AnswerAsync(deps, member, item.Uid, PartyDiceRolls.ChoicePass);
            await PartyDiceService.AnswerAsync(deps, killer, item.Uid, PartyDiceRolls.ChoicePass);
            Assert.False(PartyDiceService.IsRolling(item.Uid));
            Assert.True(GroundItemService.TryGetCorpseLoot(map, handle, member.CharacterId, death, out var left));
            Assert.Equal(new[] { item with { DiceBound = false } }, left.Items);
        }
        finally
        {
            GroundItemService.RemoveCorpse(map, handle);
        }
    }
}
