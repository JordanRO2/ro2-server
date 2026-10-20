using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// A party member locked by 2360 kind 1 (away from the world) is dropped once it has stayed away for
/// PartyService.LockedMemberLifetime (PartyService.ExpireLockedMembersAsync). A leader dropped this way is succeeded
/// by the first member in roster order who is in the world, announced with 2365 first. A drop that leaves one member
/// ends the party with 2358. See PartyNotifyOutPackets.KindLocked.
/// </summary>
public class PartyLockExpiryTests
{
    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }

 /// Records every formatted log line, so a test can see which path ran and in what order, on a service whose
 /// BroadcastService has no server to send through.
    private sealed class RecordingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines) Lines.Add(formatter(state, exception));
        }
    }

    private static (PlayerStateManager Players, PartyService Service, RecordingLogger<PartyService> Log) NewPartyService()
    {
        var players = new PlayerStateManager(new NoDirtySink());
        var log = new RecordingLogger<PartyService>();
        var service = new PartyService(players,
            new BroadcastService(players, Microsoft.Extensions.Logging.Abstractions.NullLogger<BroadcastService>.Instance),
            log);
        return (players, service, log);
    }

    private static PartyState NewParty(uint leader, params PlayerState[] members)
    {
        var party = new PartyState
        {
            PartyId = 0x40A, LeaderSessionId = leader, FounderGid = members[0].CharacterUid,
            MemberSessionIds = members.Select(m => m.SessionId).ToList(),
        };
        byte slot = 1;
        foreach (var m in members)
        {
            party.Members[m.SessionId] = m;
            party.FrameSlots[m.SessionId] = slot++;
        }
        return party;
    }

 /// The leader drops off the network and stays away. Until the lifetime expires it remains leader; at the deadline
 /// leadership goes to the first member in roster order who is in the world, before that member is removed. The
 /// character is in no party when it returns.
    [Fact]
    public async Task LockedLeader_IsDroppedAtTheDeadline_AfterHandingOverToTheFirstMemberInTheWorld()
    {
        var (players, service, log) = NewPartyService();
        var leader = new PlayerState { SessionId = 9, CharacterUid = 0x0DE0B6B3A7ECF18DUL, Name = "Leader", Level = 40 };
        var away = new PlayerState { SessionId = 5, CharacterUid = 0x0DE0B6B3A7E624AFUL, Name = "Away", Level = 30 };
        var first = new PlayerState { SessionId = 7, CharacterUid = 0x0DE0B6B3A79FF424UL, Name = "First", Level = 35 };
        var second = new PlayerState { SessionId = 8, CharacterUid = 0x0DE0B6B3A7F4678CUL, Name = "Second", Level = 33 };
        players.AddPlayer(9, leader);
        players.AddPlayer(7, first);
        players.AddPlayer(8, second);   
        var party = NewParty(9, leader, away, first, second);
        service.Track(party);

        
        players.RemovePlayer(9, out _);
        await service.NotifyMemberOfflineAsync(leader);
        var since = party.LockedSince[leader.CharacterUid];

        Assert.Equal(0, await service.ExpireLockedMembersAsync(since + PartyService.LockedMemberLifetime - TimeSpan.FromSeconds(1)));
        Assert.Equal(new List<uint> { 9, 5, 7, 8 }, party.MemberSessionIds);
        Assert.Equal(9u, party.LeaderSessionId);

        Assert.Equal(1, await service.ExpireLockedMembersAsync(since + PartyService.LockedMemberLifetime));
        Assert.Equal(new List<uint> { 5, 7, 8 }, party.MemberSessionIds);
        Assert.Equal(7u, party.LeaderSessionId);
        Assert.Equal(0u, service.FindMemberByGid(party, leader.CharacterUid));
        Assert.True(party.LockedSince.IsEmpty);
        var handover = log.Lines.FindIndex(l => l.Contains("stayed away too long"));
        var dropped = log.Lines.FindIndex(l => l.Contains("dropped from party"));
        Assert.InRange(handover, 0, dropped - 1);

        
        Assert.Equal(0, await service.ExpireLockedMembersAsync(since + 2 * PartyService.LockedMemberLifetime));

        
        var back = new PlayerState { SessionId = 20, CharacterUid = leader.CharacterUid, Name = "Leader", Level = 40 };
        players.AddPlayer(20, back);
        Assert.False(await service.RebindReturningMemberAsync(back));
        Assert.Null(service.GetPlayerParty(20));
    }

 /// The only other member stays away: its removal leaves one member, so the party ends.
    [Fact]
    public async Task DropThatLeavesOneMember_DisbandsTheParty()
    {
        var (players, service, log) = NewPartyService();
        var stay = new PlayerState { SessionId = 7, CharacterUid = 0x0DE0B6B3A79FF424UL, Name = "Stay" };
        var gone = new PlayerState { SessionId = 9, CharacterUid = 0x0DE0B6B3A7E624AFUL, Name = "Gone" };
        players.AddPlayer(7, stay);
        players.AddPlayer(9, gone);
        var party = NewParty(7, stay, gone);
        service.Track(party);

        players.RemovePlayer(9, out _);
        await service.NotifyMemberOfflineAsync(gone);
        var since = party.LockedSince[gone.CharacterUid];

        Assert.Equal(1, await service.ExpireLockedMembersAsync(since + PartyService.LockedMemberLifetime));
        Assert.Null(service.GetPlayerParty(7));
        Assert.Contains(log.Lines, l => l.Contains("disbanded"));
        Assert.DoesNotContain(log.Lines, l => l.Contains("stayed away too long")); 
    }

 /// A member that returns before the deadline is not dropped. Going away again starts a new deadline from the new lock.
    [Fact]
    public async Task MemberBackInTime_IsKept_AndASecondLockStartsANewDeadline()
    {
        var (players, service, _) = NewPartyService();
        var stay = new PlayerState { SessionId = 7, CharacterUid = 0x0DE0B6B3A79FF424UL, Name = "Stay" };
        var away = new PlayerState { SessionId = 9, CharacterUid = 0x0DE0B6B3A7E624AFUL, Name = "Away" };
        players.AddPlayer(7, stay);
        players.AddPlayer(9, away);
        var party = NewParty(7, stay, away);
        service.Track(party);

        players.RemovePlayer(9, out _);
        await service.NotifyMemberOfflineAsync(away);
        var firstLock = party.LockedSince[away.CharacterUid];

        
        var back = new PlayerState { SessionId = 20, CharacterUid = away.CharacterUid, Name = "Away" };
        players.AddPlayer(20, back);
        Assert.True(await service.RebindReturningMemberAsync(back));
        Assert.False(party.LockedSince.ContainsKey(away.CharacterUid));
        Assert.Equal(0, await service.ExpireLockedMembersAsync(firstLock + 2 * PartyService.LockedMemberLifetime));
        Assert.Equal(new List<uint> { 7, 20 }, party.MemberSessionIds);

        
        players.RemovePlayer(20, out _);
        await service.NotifyMemberOfflineAsync(back);
        var secondLock = party.LockedSince[away.CharacterUid];
        Assert.True(secondLock >= firstLock);
        Assert.Equal(0, await service.ExpireLockedMembersAsync(secondLock + PartyService.LockedMemberLifetime - TimeSpan.FromSeconds(1)));
        Assert.Equal(1, await service.ExpireLockedMembersAsync(secondLock + PartyService.LockedMemberLifetime));
        Assert.Null(service.GetPlayerParty(7));
    }

 /// A member that went to character select with 1418 keeps its deadline on the detached key, matched by GID. A member
 /// that left with 1384 before its deadline leaves nothing to drop.
    [Fact]
    public async Task DetachedMember_IsDroppedByGid_AndOneThatLeftFirstIsNot()
    {
        var (players, service, _) = NewPartyService();
        var stay = new PlayerState { SessionId = 7, CharacterUid = 0x0DE0B6B3A79FF424UL, Name = "Stay" };
        var switcher = new PlayerState { SessionId = 11, CharacterUid = 0x0DE0B6B3A7F4678CUL, Name = "Switcher" };
        var third = new PlayerState { SessionId = 12, CharacterUid = 0x0DE0B6B3A7E624AFUL, Name = "Third" };
        players.AddPlayer(7, stay);
        players.AddPlayer(11, switcher);
        players.AddPlayer(12, third);
        var party = NewParty(7, stay, switcher, third);
        service.Track(party);

        
        await service.NotifyMemberSwitchedCharacterAsync(switcher);
        players.RemovePlayer(11, out _);
        var key = service.FindMemberByGid(party, switcher.CharacterUid);
        Assert.NotEqual(11u, key);
        var since = party.LockedSince[switcher.CharacterUid];

        
        
        players.RemovePlayer(12, out _);
        await service.NotifyMemberOfflineAsync(third);
        var thirdSince = party.LockedSince[third.CharacterUid];
        var relog = new PlayerState { SessionId = 30, CharacterUid = third.CharacterUid, Name = "Third" };
        players.AddPlayer(30, relog);
        Assert.Equal(1, await service.LeaveAsOfflineMemberAsync(relog));

        var deadline = (thirdSince > since ? thirdSince : since) + PartyService.LockedMemberLifetime;
        Assert.Equal(1, await service.ExpireLockedMembersAsync(deadline));
        Assert.Equal(0u, service.FindMemberByGid(party, switcher.CharacterUid));
        Assert.Null(service.GetPlayerParty(key));
        Assert.Null(service.GetPlayerParty(7)); 
    }
}
