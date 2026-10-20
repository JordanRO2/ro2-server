using System.Buffers.Binary;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// A character that its party holds as an offline member is bound back to that party when it next loads a map
/// (PartyService.RebindReturningMemberAsync, called from the 1304 AnsMapLoading handler). Membership is keyed by
/// character, so the roster position, frame slot and leadership carry over to the new session.
/// </summary>
public class PartyRebindTests
{
    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }

 /// Records every formatted log line, so a test can see which path ran on a service whose
 /// BroadcastService has no server to send through.
    private sealed class RecordingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
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

 /// A member that returns as the same character on the same session takes back its roster position, frame slot and
 /// leadership, so leader-gated requests work again. A second map load changes nothing. An offline member relogging on a
 /// new session is bound the same way; a character no party holds is left alone.
    [Fact]
    public async Task SameCharacterBack_IsBoundAgain_WithPositionSlotAndLeadership()
    {
        var (players, service, log) = NewPartyService();

        const ulong switcherGid = 0x0DE0B6B3A7F4678CUL;
        const ulong goneGid = 0x0DE0B6B3A7E624AFUL;
        const ulong otherGid = 0x0DE0B6B3A79FF424UL;
        var switcher = new PlayerState { SessionId = 11, CharacterUid = switcherGid, Name = "Switcher", Level = 30, MapId = 1100 };
        var gone = new PlayerState { SessionId = 9, CharacterUid = goneGid, Name = "Gone", Level = 50, MapId = 4151 };
        var other = new PlayerState { SessionId = 7, CharacterUid = otherGid, Name = "Other", Level = 40, MapId = 1115 };
        players.AddPlayer(11, switcher);
        players.AddPlayer(7, other);   

        var party = new PartyState
        {
            PartyId = 0x30E, LeaderSessionId = 11, FounderGid = switcherGid, MemberSessionIds = [11, 9, 7],
        };
        party.Members[11] = switcher;
        party.Members[9] = gone;
        party.Members[7] = other;
        party.FrameSlots[11] = 1;
        party.FrameSlots[9] = 3;
        party.FrameSlots[7] = 2;
        service.Track(party);

        
        await service.NotifyMemberSwitchedCharacterAsync(switcher);
        players.RemovePlayer(11, out _);
        var detachedKey = service.FindMemberByGid(party, switcherGid);
        Assert.NotEqual(11u, detachedKey);
        var back = new PlayerState { SessionId = 11, CharacterUid = switcherGid, Name = "Switcher", Level = 30, MapId = 1100 };
        players.AddPlayer(11, back);
        Assert.Null(service.GetPlayerParty(11));

        
        Assert.True(await service.RebindReturningMemberAsync(back));
        Assert.Single(log.Lines, l => l.Contains("bound again"));
        Assert.Same(party, service.GetPlayerParty(11));
        Assert.Null(service.GetPlayerParty(detachedKey));
        Assert.Equal(new List<uint> { 11, 9, 7 }, party.MemberSessionIds);
        Assert.Equal(11u, party.LeaderSessionId);
        Assert.Equal(1, PartyService.GetFrameSlot(party, 11));
        Assert.Equal(0, PartyService.GetFrameSlot(party, detachedKey));
        Assert.Same(back, party.Members[11]);
        Assert.Same(back, service.GetOnlineMember(party, 11));
        Assert.Equal(11u, service.FindMemberByGid(party, switcherGid));

        
        
        var body = service.BuildRosterBody(party);
        Assert.Equal(PlayerStateManager.BuildActorHandle(11), BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(7 + 63)));
        Assert.Equal(switcherGid, BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(7 + 63 + 4)));
        Assert.Equal(switcherGid, BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(7 + 12))); 

        
        await service.ChangeLeaderAsync(11, PlayerStateManager.BuildActorHandle(11), otherGid);
        Assert.Equal(7u, party.LeaderSessionId);

        
        Assert.False(await service.RebindReturningMemberAsync(back));
        Assert.Single(log.Lines, l => l.Contains("bound again"));

        
        var relogged = new PlayerState { SessionId = 20, CharacterUid = goneGid, Name = "Gone", Level = 50, MapId = 4151 };
        players.AddPlayer(20, relogged);
        Assert.True(await service.RebindReturningMemberAsync(relogged));
        Assert.Equal(new List<uint> { 11, 20, 7 }, party.MemberSessionIds);
        Assert.Equal(3, PartyService.GetFrameSlot(party, 20));
        Assert.Null(service.GetPlayerParty(9));
        Assert.Same(party, service.GetPlayerParty(20));

        
        var stranger = new PlayerState { SessionId = 30, CharacterUid = 0x0DE0B6B3A7000001UL, Name = "Stranger", Level = 1 };
        players.AddPlayer(30, stranger);
        Assert.False(await service.RebindReturningMemberAsync(stranger));
        Assert.Null(service.GetPlayerParty(30));
    }

 /// A 1418 to another character leaves that character in no party and does not bind it to the old party at map load.
 /// The old character stays on its detached key.
    [Fact]
    public async Task DifferentCharacterAfter1418_IsNotBound()
    {
        var (players, service, _) = NewPartyService();

        var switcher = new PlayerState { SessionId = 11, CharacterUid = 0x0DE0B6B3A7F4678CUL, Name = "Switcher" };
        var other = new PlayerState { SessionId = 7, CharacterUid = 0x0DE0B6B3A79FF424UL, Name = "Other" };
        players.AddPlayer(11, switcher);
        players.AddPlayer(7, other);
        var party = new PartyState { PartyId = 5, LeaderSessionId = 7, FounderGid = other.CharacterUid, MemberSessionIds = [7, 11] };
        party.Members[7] = other;
        party.Members[11] = switcher;
        party.FrameSlots[7] = 1;
        party.FrameSlots[11] = 2;
        service.Track(party);

        await service.NotifyMemberSwitchedCharacterAsync(switcher);
        players.RemovePlayer(11, out _);
        var key = service.FindMemberByGid(party, switcher.CharacterUid);
        var next = new PlayerState { SessionId = 11, CharacterUid = 0x0DE0B6B3A7F4678DUL, Name = "Next" };
        players.AddPlayer(11, next);

        Assert.False(await service.RebindReturningMemberAsync(next));
        Assert.Null(service.GetPlayerParty(11));
        Assert.Equal(new List<uint> { 7, key }, party.MemberSessionIds);
        Assert.Null(service.GetOnlineMember(party, key));
    }
}
