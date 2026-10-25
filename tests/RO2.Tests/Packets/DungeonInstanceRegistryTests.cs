using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Characters.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// Tests the instance bookkeeping behind 1521, 1531, 1534, 3035 and 2760 (<see cref="DungeonInstanceRegistry"/>),
/// replayed on representative sequences. Entry counts are kept in a dictionary, standing in for
/// DungeonEntryCountRepository within one daily window.
/// </summary>
public class DungeonInstanceRegistryTests
{
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    private const uint Character = 0xA7E8421Cu;
    private const uint WorldMap = 1221;

 /// <summary>
 /// A solo character's dungeon session: three entries, three resets, the 3035 answers between them, and the
 /// 19015 that ends it. Each leave of an instance is a 2556 back to map 1221.
 /// </summary>
    [Fact]
    public async Task Replay_d414c84e_2ce1e0c0_EntriesResetsCountsAndTheRefusal()
    {
        var data = await DungeonEntryGateTests.DungeonDataAsync();
        var registry = new DungeonInstanceRegistry();
        var owner = DungeonInstanceOwner.For(null, Character);
        var used = new Dictionary<uint, int>();

        ushort Enter(int group, int level)
        {
            var row = DungeonEntryGateTests.Row(data, group, level);
            var wouldCount = registry.WouldCount(owner, row.DungeonID, Character);
            var code = DungeonEntryGate.CheckInstanceEntry(row, 50, true, wouldCount, used.GetValueOrDefault(row.DungeonID));
            if (code != 0) return code;
            var entry = registry.Enter(owner, row.DungeonID, data.Positions[row.Position_ID].Area_ID, Character);
            Assert.True(entry.IsNewInstance);
            if (entry.CountsForCharacter) used[row.DungeonID] = used.GetValueOrDefault(row.DungeonID) + 1;
            return 0;
        }
        string Answer3035(uint dungeonId)
            => Hex(DungeonEntranceInfoHandler.Build3035(dungeonId, (ushort)used.GetValueOrDefault(dungeonId), 2));
        string[] Reset() => registry.Reset(owner, WorldMap, Character).Select(id => Hex(DungeonResetHandler.Build2771(id))).ToArray();

        Assert.Equal(0, Enter(42, 3));                                         
        Assert.Equal(new[] { "42000000" }, Reset());                           
        Assert.Empty(Reset());                                                 
        Assert.Empty(Reset());                                                 
        Assert.Equal("4100000000000200", Answer3035(0x41));                    
        Assert.Equal("4200000001000200", Answer3035(0x42));                    
        Assert.Equal("4000000000000200", Answer3035(0x40));                    
        Assert.Equal(0, Enter(42, 1));                                         
        Assert.Equal(new[] { "40000000" }, Reset());                           
        Assert.Empty(Reset());                                                 
        Assert.Equal("4000000001000200", Answer3035(0x40));                    
        Assert.Equal(0, Enter(42, 1));                                         
        Assert.Equal(new[] { "40000000" }, Reset());                           
        Assert.Empty(Reset());                                                 
        Assert.Equal("4000000002000200", Answer3035(0x40));                    
        Assert.Equal(DungeonEntryGate.ResultEnterCountExceeded, Enter(42, 1)); 
        Assert.Equal("4100000000000200", Answer3035(0x41));                    
    }

    [Fact]
    public void Reset_FromInsideAnOpenInstance_IsSilentAndKeepsIt()
    {
        
        var registry = new DungeonInstanceRegistry();
        var owner = DungeonInstanceOwner.For(null, Character);
        registry.Enter(owner, 64, 426101, Character);

        Assert.Empty(registry.Reset(owner, 426101, Character));
        Assert.Equal(new byte[] { 64 }, registry.OpenDungeonIds(owner));
        Assert.Equal(new byte[] { 64 }, registry.Reset(owner, WorldMap, Character));
        Assert.Empty(registry.OpenDungeonIds(owner));
    }

    [Fact]
    public void Reset_NamesEveryOpenInstanceAscending()
    {
        
        var registry = new DungeonInstanceRegistry();
        var owner = DungeonInstanceOwner.For(null, Character);
        registry.Enter(owner, 63, 410104, Character);
        registry.Enter(owner, 8, 412103, Character);

        var reset = registry.Reset(owner, 1111, Character);
        Assert.Equal(new[] { "08000000", "3f000000" }, reset.Select(id => Hex(DungeonResetHandler.Build2771(id))));
    }

    [Fact]
    public void PartyInstance_OpensOnce_CountsEachMemberOnce_AndClosesAtTheLastMembersReset()
    {
        var registry = new DungeonInstanceRegistry();
        var party = new PartyState { PartyId = 0x00026915 };
        var owner = DungeonInstanceOwner.For(party, 1);
        const uint leader = 1, member = 2;

        
        var first = registry.Enter(owner, 66, 426103, leader);
        Assert.True(first.IsNewInstance);
        Assert.True(first.CountsForCharacter);
        
        Assert.True(registry.WouldCount(owner, 66, member));
        var joined = registry.Enter(owner, 66, 426103, member);
        Assert.False(joined.IsNewInstance);
        Assert.True(joined.CountsForCharacter);
        Assert.Equal(first.InstanceId, joined.InstanceId);
        
        Assert.False(registry.WouldCount(owner, 66, leader));
        var again = registry.Enter(owner, 66, 426103, leader);
        Assert.False(again.IsNewInstance);
        Assert.False(again.CountsForCharacter);

        
        
        
        Assert.Equal(new byte[] { 66 }, registry.Reset(owner, WorldMap, leader));
        Assert.True(registry.IsOpen(first.InstanceId));
        Assert.False(registry.IsBound(first.InstanceId, leader));
        Assert.True(registry.IsBound(first.InstanceId, member));
        Assert.Empty(registry.Reset(owner, WorldMap, leader));
        Assert.Equal(new byte[] { 66 }, registry.Reset(owner, WorldMap, member));
        Assert.False(registry.IsOpen(first.InstanceId));
        Assert.Empty(registry.OpenDungeonIds(owner));

        
        
        var reopened = registry.Enter(owner, 66, 426103, leader);
        Assert.True(reopened.IsNewInstance);
        Assert.True(reopened.CountsForCharacter);
        Assert.NotEqual(first.InstanceId, reopened.InstanceId);
    }

    [Fact]
    public void ChangedPartyContext_FindsNothingToReset()
    {
        
        
        var registry = new DungeonInstanceRegistry();
        var party = new PartyState { PartyId = 7 };
        var inParty = DungeonInstanceOwner.For(party, Character);
        var solo = DungeonInstanceOwner.For(null, Character);
        registry.Enter(inParty, 64, 426101, Character);

        Assert.Empty(registry.Reset(solo, WorldMap, Character));
        Assert.True(registry.WouldCount(solo, 64, Character));
        Assert.True(registry.Enter(solo, 64, 426101, Character).IsNewInstance);
        
        Assert.NotEqual(DungeonInstanceOwner.For(new PartyState { PartyId = Character }, 1), solo);
    }

    [Fact]
    public void NotifyCopies_AreTheInWorldMembers()
    {
        
        
        var players = new PlayerStateManager(new NoDirtySink());
        var party = new PartyState { PartyId = 1, LeaderSessionId = 10 };
        foreach (var session in new uint[] { 10, 11, 12 })
        {
            party.MemberSessionIds.Add(session);
            players.AddPlayer(session, new PlayerState { SessionId = session });
        }
        party.MemberSessionIds.Add(0x8000_0013); 

        Assert.Equal(3, DungeonLevelSelectInfoNotify.InWorldMemberCount(party, players));
        Assert.Equal("2a0000000300", Hex(DungeonLevelSelectInfoNotify.Build2760(0x2a, 3)));
    }

    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }
}
