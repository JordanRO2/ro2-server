using System.Buffers.Binary;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Crash-sweep gate for the party-state S2C family. Each opcode here once crashed the remote client: the handler
/// sent a raw body (no compact-length marker) to an enveloped opcode, so the client's decoder read an invalid first
/// byte and the per-opcode stub dereferenced a null blob.
/// </summary>
/// <remarks>
/// Invariant: the first byte of every S2C body the handler can emit is a valid compact marker in {1,2,4,8}. For
/// compact byte-array opcodes the declared length, the self-length echo and the msgTag at body+4 are checked too.
/// Assertion 2622 is byte-exact against its constant empty answer.
/// </remarks>
public class PartyCrashSweepTests
{
    private static readonly int[] ValidMarkers = [1, 2, 4, 8];

 /// Decodes a Proud compact length: returns (value, bytesConsumed) and asserts the marker is valid.
    private static (long value, int consumed) ReadCompactLength(ReadOnlySpan<byte> b)
    {
        int n = b[0];
        Assert.Contains(n, ValidMarkers);
        long v = n switch
        {
            1 => b[1],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(1)),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(1)),
            _ => (long)BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(1)),
        };
        return (v, 1 + n);
    }

 /// First body byte must be a valid compact marker (the crash-safety invariant).
    private static void AssertFirstByteSafe(byte[] body)
    {
        Assert.NotEmpty(body);
        Assert.Contains((int)body[0], ValidMarkers);
    }

 /// Compact byte array: the leading compact length equals the following byte count and the u16 self-length, and the
 /// msgTag at offset 2 after the self-length matches. A self-length of 2 plus the payload length is implied.
    private static void AssertCompactBa(byte[] body, ushort expectedTag)
    {
        AssertFirstByteSafe(body);
        var (len, consumed) = ReadCompactLength(body);
        int following = body.Length - consumed;
        Assert.Equal(following, (int)len);
        ushort selfLen = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(consumed));
        Assert.Equal((int)len, selfLen);
        ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(consumed + 2));
        Assert.Equal(expectedTag, tag);
        
        
        Assert.True(selfLen >= 4);
    }

    [Fact]
    public void OnPartyJoin_2347_CompactBa()
    {
        var body = PartyStatePackets.BuildOnPartyJoin(1u, 2UL, 0, "Tester");
        AssertCompactBa(body, 58141);
        Assert.Equal(61, body.Length); 
    }

    [Fact]
    public void OnPartyError_2348_CompactBa()
    {
        var body = PartyStatePackets.BuildOnPartyError(1u, 15715, "Tester");
        AssertCompactBa(body, 58143);
        Assert.Equal(55, body.Length); 
    }

    [Fact]
    public void OnPartyOut_2349_CompactBa()
    {
        var body = PartyAnswerPackets.BuildResultAck(PartyStatePackets.MsgTagOnPartyOut, 42u);
        AssertCompactBa(body, 58145);
        Assert.Equal(12, body.Length); 
    }

    [Fact]
    public void OnPartyChangeItem_2350_CompactBa()
    {
        var body = PartyAnswerPackets.BuildResultAck(PartyStatePackets.MsgTagOnPartyChangeItem, 1u);
        AssertCompactBa(body, 58147);
        Assert.Equal(12, body.Length); 
    }

    [Fact]
    public void OnPartyChangeMaster_2352_CompactBa()
    {
        var body = PartyStatePackets.BuildOnPartyChangeMasterAck(7u);
        AssertCompactBa(body, 58151);
        Assert.Equal(12, body.Length);
    }

    [Fact]
    public void NfyPartyTypeChange_2692_CompactBa()
    {
        var body = PartyStatePackets.BuildNfyPartyTypeChange(2);
        AssertCompactBa(body, 58436);
    }

    [Fact]
    public void NfyPartyFightingTrimStart_2699_CompactBa()
    {
        var body = PartyReadyCheckPackets.BuildNfyPartyFightingTrimStart2699(5ul);
        AssertCompactBa(body, 58432);
    }

    [Fact]
    public void NfyPartySubMaster_2703_CompactBa()
    {
        var body = PartyStatePackets.BuildNfyPartySubMaster(0x0DE0B6B3A7C45ED7UL);
        AssertCompactBa(body, 58433);
        Assert.Equal(18, body.Length); 
    }

    [Fact]
    public void NfyPartyFrameMove_2705_CompactBa()
    {
        var body = PartyAnswerPackets.BuildNfyPartyFrameMove2705(9UL, 3, 0UL, 0);
        AssertCompactBa(body, 58434);
        Assert.Equal(28, body.Length); 
    }

    [Fact]
    public void NfyPartyDismissSubmaster_2707_CompactBa()
    {
        var body = PartyStatePackets.BuildNfyPartyDismissSubmaster(0x0DE0B6B3A7E6F919UL);
        AssertCompactBa(body, 58435);
        Assert.Equal(18, body.Length); 
    }

    [Fact]
    public void NfyPartyDiceChattingCmd_2788_CompactBa()
    {
        var body = PartyStatePackets.BuildNfyPartyDiceChattingCmd(0x0DE0B6B3A7E242D6UL, 1, 100, 35);
        AssertCompactBa(body, 58478);
        Assert.Equal(26, body.Length); 
    }

    [Fact]
    public void NfyPartyDiceChattingCmd_2788_MatchesLiveFrame()
    {
        
        var body = PartyStatePackets.BuildNfyPartyDiceChattingCmd(0x0DE0B6B3A7E242D6UL, 1, 100, 0x23);
        Assert.Equal("011818006ee4000000000000d642e2a7b3b6e00d010064002300",
            Convert.ToHexString(body).ToLowerInvariant());
    }

    
    
    [Fact]
    public void OnPartyInvite_2346_MatchesRetailRefusal()
    {
        
        var body = PartyStatePackets.BuildOnPartyInvite(0x0BFEEB11u, 15710, "PartTimeKickah", 2);
        Assert.Equal("013535001ce311ebfe0b5e3d5000610072007400540069006d0065004b00690063006b0061006800000000000000000000000000000002",
            Convert.ToHexString(body).ToLowerInvariant());
    }

    [Fact]
    public void OnPartyJoin_2347_MatchesRetailDialog()
    {
        var body = PartyStatePackets.BuildOnPartyJoin(0x071BE811u, 0x0DE0B6B3A79883F2UL, 2, "buddyy");
        Assert.Equal("013b3b001de311e81b07f28398a7b3b6e00d02620075006400640079007900000000000000000000000000000000000000000000000000000000000000",
            Convert.ToHexString(body).ToLowerInvariant());
    }

    [Fact]
    public void OnPartyError_2348_MatchesRetailFrame()
    {
        var body = PartyStatePackets.BuildOnPartyError(0x0A4B1E11u, 5, "MUSAVADA");
        Assert.Equal("013535001fe3111e4b0a0500004d0055005300410056004100440041000000000000000000000000000000000000000000000000000000",
            Convert.ToHexString(body).ToLowerInvariant());
    }

    [Theory]
    [InlineData(PartyStatePackets.MsgTagOnPartyOut, 0x071BE811u, "010a0a0021e311e81b070000")]            
    [InlineData(PartyStatePackets.MsgTagOnPartyChangeItem, 0x0954CB11u, "010a0a0023e311cb54090000")]     
    [InlineData(PartyAnswerPackets.MsgTagOnPartyExpulsion, 0x0A4B1E11u, "010a0a0029e3111e4b0a0000")]     
    public void ResultAcks_MatchRetailFrames(ushort msgTag, uint handle, string retail)
    {
        Assert.Equal(retail, Convert.ToHexString(PartyAnswerPackets.BuildResultAck(msgTag, handle)).ToLowerInvariant());
    }

    [Fact]
    public void OnPartyChangeMasterAck_2352_IsTheResultAckShape()
    {
        
        
        Assert.Equal("010a0a0027e311fc89070000",
            Convert.ToHexString(PartyStatePackets.BuildOnPartyChangeMasterAck(0x0789FC11u)).ToLowerInvariant());
    }

    [Fact]
    public void OnPartyNotifyDestroy_2358_MatchesRetailFrame()
    {
        var body = PartyStatePackets.BuildOnPartyNotifyDestroy2358(0x0011FB15u);
        Assert.Equal("010c0c0032e30000000015fb1100", Convert.ToHexString(body).ToLowerInvariant());
    }

    [Fact]
    public void OnPartyNotifyChangeItem_2363_MatchesRetailFrame()
    {
        var body = PartyAnswerPackets.BuildPartyNotifyChangeItem2363(0x00135115u, 3, 0);
        Assert.Equal("0116160037e3000000001551130003000000000000000000", Convert.ToHexString(body).ToLowerInvariant());
    }

    [Fact]
    public void OnPartyNotifyChangeMaster_2365_MatchesRetailFrame()
    {
        var body = PartyStatePackets.BuildOnPartyNotifyChangeMaster2365(0x0011FB15u, 0x0DE0B6B3A7D48A64UL);
        Assert.Equal("0114140039e30000000015fb1100648ad4a7b3b6e00d", Convert.ToHexString(body).ToLowerInvariant());
    }

    [Fact]
    public void OnPartyNotifyExpulsion_2366_MatchesRetailFrame()
    {
        var body = PartyAnswerPackets.BuildPartyNotifyExpulsion2366(0x00121215u, 0x0DE0B6B3A7E5067DUL);
        Assert.Equal("011414003ae300000000151212007d06e5a7b3b6e00d", Convert.ToHexString(body).ToLowerInvariant());
    }

    [Fact]
    public void PartyTypeChange_2691_2692_MatchRetailFrames()
    {
        Assert.Equal("000002", Convert.ToHexString(PartyStatePackets.BuildAnsPartyTypeChange(2)).ToLowerInvariant());
        Assert.Equal("0109090044e40000000002", Convert.ToHexString(PartyStatePackets.BuildNfyPartyTypeChange(2)).ToLowerInvariant());
    }

    [Fact]
    public void NfyPartyFrameMove_2705_MatchesRetailSwap()
    {
        var body = PartyAnswerPackets.BuildNfyPartyFrameMove2705(0x37A661C10D6E582BUL, 5, 0x0DE0B6B3A7E9E0A4UL, 2);
        Assert.Equal("011a1a0042e4000000002b586e0dc161a63705a4e0e9a7b3b6e00d02", Convert.ToHexString(body).ToLowerInvariant());
    }

    
    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(RO2.Server.Zone.Models.PlayerState player) { }
        public void Forget(uint characterId) { }
    }

 /// The 2361 roster carries the loot rule, each member's frame slot, and an offline member with its real id and last
 /// state. A bound session that carries another character must not be taken for the member that joined on it.
    [Fact]
    public void Roster_2361_CarriesLootRule_FrameSlots_AndOfflineMembers()
    {
        var players = new PlayerStateManager(new NoDirtySink());
        var service = new PartyService(players,
            new BroadcastService(players, Microsoft.Extensions.Logging.Abstractions.NullLogger<BroadcastService>.Instance),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PartyService>.Instance);

        var leader = new RO2.Server.Zone.Models.PlayerState
        {
            SessionId = 7, CharacterUid = 0x0DE0B6B3A79FF424UL, Name = "Leader", Level = 40, MapId = 1115,
        };
        var gone = new RO2.Server.Zone.Models.PlayerState
        {
            SessionId = 9, CharacterUid = 0x0DE0B6B3A7E624AFUL, Name = "Gone", Level = 50, MapId = 4151,
        };
        var joinedOn11 = new RO2.Server.Zone.Models.PlayerState
        {
            SessionId = 11, CharacterUid = 0x0DE0B6B3A7D48522UL, Name = "Switched", Level = 30, MapId = 1100,
        };
        players.AddPlayer(7, leader);
        
        players.AddPlayer(11, new RO2.Server.Zone.Models.PlayerState
        {
            SessionId = 11, CharacterUid = 0x0DE0B6B3A7000001UL, Name = "Other", Level = 1, MapId = 1100,
        });

        
        
        const ulong founderGid = 0x0DE0B6B3A7DFF6E0UL;
        var party = new PartyState
        {
            PartyId = 0x1234, LeaderSessionId = 7, PartyType = 2, ItemRule = 7, ItemQuality = 4,
            FounderGid = founderGid, MemberSessionIds = [7, 9, 11],
        };
        party.Members[7] = leader;
        party.Members[9] = gone;
        party.Members[11] = joinedOn11;
        party.FrameSlots[7] = 1;
        party.FrameSlots[9] = 12;
        party.FrameSlots[11] = 3;

        var body = service.BuildRosterBody(party);
        AssertCompactBa(body, 58165);
        var payload = body.AsSpan(7);
        Assert.Equal(2, payload[8]);   
        Assert.Equal(7, payload[10]);  
        Assert.Equal(4, payload[28]);  
        Assert.Equal(0x0DE0B6B3A79FF424UL, BinaryPrimitives.ReadUInt64LittleEndian(payload[12..])); 
        Assert.Equal(founderGid, BinaryPrimitives.ReadUInt64LittleEndian(payload[54..]));           
        Assert.Equal(3, payload[62]);  

        var recs = payload[63..];
        Assert.Equal(3 * 208, recs.Length);
        ulong Gid(int i) => BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(7 + 63 + 208 * i + 4));
        byte Slot(int i) => body[7 + 63 + 208 * i + 62];
        uint Map(int i) => BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(7 + 63 + 208 * i + 35));
        Assert.Equal(0x0DE0B6B3A79FF424UL, Gid(0));
        Assert.Equal(1, Slot(0));
        Assert.Equal(0x0DE0B6B3A7E624AFUL, Gid(1));   
        Assert.Equal(12, Slot(1));
        Assert.Equal(4151u, Map(1));
        Assert.Equal(50, body[7 + 63 + 208 + 15]);
        Assert.Equal(0x0DE0B6B3A7D48522UL, Gid(2));   
        Assert.Equal(3, Slot(2));

        Assert.Equal(new List<ulong> { 0x0DE0B6B3A79FF424UL, 0x0DE0B6B3A7E624AFUL, 0x0DE0B6B3A7D48522UL },
            service.BuildMemberGidList(party));
        Assert.Equal(9u, service.FindMemberByGid(party, 0x0DE0B6B3A7E624AFUL));
        Assert.Equal(11u, service.FindMemberByGid(party, 0x0DE0B6B3A7D48522UL));
        Assert.Equal(0u, service.FindMemberByGid(party, 0x0DE0B6B3A7000001UL));
        Assert.Same(leader, service.GetOnlineMember(party, 7));
        Assert.Null(service.GetOnlineMember(party, 9));
        Assert.Null(service.GetOnlineMember(party, 11));
    }

 /// Records every formatted log line, so a test can tell whether the lock notice path ran (it logs once per notice)
 /// on a service with no server to send through.
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

 /// 1418: the member that switched away stays in the party, locked, under a detached key with its roster position,
 /// slot and role. The session is free for the next character, which is in no party and receives no party frame until
 /// invited. That character going offline sends nothing; a 1384 from the old character on a new session removes the
 /// member it left behind and hands leadership to the online member.
    [Fact]
    public async Task Switch_1418_DetachesTheMemberFromItsSession()
    {
        var (players, service, log) = NewPartyService();

        const ulong switcherGid = 0x0DE0B6B3A7F4678CUL;
        const ulong nextGid = 0x0DE0B6B3A7F4678DUL;
        var switcher = new RO2.Server.Zone.Models.PlayerState
        {
            SessionId = 11, CharacterUid = switcherGid, Name = "Switcher", Level = 30, MapId = 1100,
        };
        var gone = new RO2.Server.Zone.Models.PlayerState
        {
            SessionId = 9, CharacterUid = 0x0DE0B6B3A7E624AFUL, Name = "Gone", Level = 50, MapId = 4151,
        };
        var other = new RO2.Server.Zone.Models.PlayerState
        {
            SessionId = 7, CharacterUid = 0x0DE0B6B3A79FF424UL, Name = "Other", Level = 40, MapId = 1115,
        };
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
        Assert.Single(log.Lines, l => l.Contains("told to lock"));
        players.RemovePlayer(11, out _);
        var next = new RO2.Server.Zone.Models.PlayerState
        {
            SessionId = 11, CharacterUid = nextGid, Name = "Next", Level = 1, MapId = 1100,
        };
        players.AddPlayer(11, next);

        Assert.Null(service.GetPlayerParty(11));
        var key = service.FindMemberByGid(party, switcherGid);
        Assert.NotEqual(0u, key);
        Assert.NotEqual(11u, key);
        Assert.Equal(new List<uint> { key, 9, 7 }, party.MemberSessionIds);   
        Assert.Same(party, service.GetPlayerParty(key));
        Assert.Equal(1, PartyService.GetFrameSlot(party, key));
        Assert.Equal(0, PartyService.GetFrameSlot(party, 11));
        Assert.Equal(key, party.LeaderSessionId);
        Assert.Same(switcher, party.Members[key]);
        Assert.Null(service.GetOnlineMember(party, key));
        Assert.Equal(0u, service.FindMemberByGid(party, nextGid));

        
        
        var body = service.BuildRosterBody(party);
        ulong Gid(int i) => BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(7 + 63 + 208 * i + 4));
        uint Handle(int i) => BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(7 + 63 + 208 * i));
        Assert.Equal(3, body[7 + 62]);
        Assert.Equal(switcherGid, Gid(0));
        Assert.Equal(1, body[7 + 63 + 62]);
        Assert.Equal((key << 8) | 0x11u, Handle(0));
        Assert.NotEqual(PlayerStateManager.BuildActorHandle(11), Handle(0));
        Assert.Equal(new List<ulong> { switcherGid, gone.CharacterUid, other.CharacterUid },
            service.BuildMemberGidList(party));

        
        await service.NotifyMemberOfflineAsync(next);
        await service.NotifyMemberSwitchedCharacterAsync(next);
        Assert.Single(log.Lines, l => l.Contains("told to lock"));
        Assert.Same(switcher, party.Members[key]);
        Assert.Equal(new List<uint> { key, 9, 7 }, party.MemberSessionIds);

        
        
        var back = new RO2.Server.Zone.Models.PlayerState
        {
            SessionId = 20, CharacterUid = switcherGid, Name = "Switcher", Level = 30, MapId = 1100,
        };
        players.AddPlayer(20, back);
        Assert.Equal(0, await service.LeaveAsOfflineMemberAsync(next));
        Assert.Equal(1, await service.LeaveAsOfflineMemberAsync(back));
        Assert.Null(service.GetPlayerParty(key));
        Assert.Equal(new List<uint> { 9, 7 }, party.MemberSessionIds);
        Assert.Equal(7u, party.LeaderSessionId);
        Assert.Same(party, service.GetPlayerParty(7));
    }

 /// A session still bound to a party while it carries another character: that character going offline sends no lock
 /// notice (no roster record holds its id) and keeps the member's record.
    [Fact]
    public async Task Offline_OnASessionHoldingAnotherMember_SendsNothing()
    {
        var (_, service, log) = NewPartyService();

        var joined = new RO2.Server.Zone.Models.PlayerState
        {
            SessionId = 11, CharacterUid = 0x0DE0B6B3A7D48522UL, Name = "Joined", Level = 30,
        };
        var other = new RO2.Server.Zone.Models.PlayerState
        {
            SessionId = 11, CharacterUid = 0x0DE0B6B3A7000001UL, Name = "Other", Level = 1,
        };
        var party = new PartyState { PartyId = 5, LeaderSessionId = 7, MemberSessionIds = [7, 11] };
        party.Members[11] = joined;
        service.Track(party);

        await service.NotifyMemberOfflineAsync(other);
        Assert.DoesNotContain(log.Lines, l => l.Contains("told to lock"));
        Assert.Same(joined, party.Members[11]);
        Assert.Equal(11u, service.FindMemberByGid(party, joined.CharacterUid));

        
        await service.NotifyMemberOfflineAsync(joined);
        Assert.Single(log.Lines, l => l.Contains("told to lock"));
    }

    
    
    [Fact]
    public void AnsPartyApplySearch_2622_EmptyAnswer_ByteExact()
    {
        var body = PartyStatePackets.BuildAnsPartyApplySearchEmpty();
        AssertFirstByteSafe(body);
        Assert.Equal("01050100010000", Convert.ToHexString(body).ToLowerInvariant());
        
        var (len, consumed) = ReadCompactLength(body);
        Assert.Equal(5, (int)len);
        Assert.Equal((int)len, body.Length - consumed);
    }
    
    
    
    
    [Fact]
    public void AnsRandomItemSelected_2714_ReaderLayout()
    {
        var body = RandomBoxPackets.Build2714(70000362u, RandomBoxPackets.ResultOk);
        Assert.Equal(10, body.Length);
        Assert.Equal("ea1e2c04" + "00000000" + "0000", Convert.ToHexString(body).ToLowerInvariant());

        var bagFull = RandomBoxPackets.Build2714(0, RandomBoxPackets.ResultBagFull);
        Assert.Equal("00000000" + "00000000" + "015a", Convert.ToHexString(bagFull).ToLowerInvariant());
    }

    [Fact]
    public void RandomItemBatting_1514_FindsTheBagStackOfTheTypeAndSet()
    {
        var items = new Dictionary<uint, ItemData>
        {
            [60000408] = new ItemData { ID = 60000408, Item_Type = 23, RandomSet_ID = 362 },
            [70000334] = new ItemData { ID = 70000334, Item_Type = 59, RandomSet_ID = 1 },
        };
        var box = new InventoryItem { Id = 5, ItemId = 60000408, Count = 3, Slot = 4, StorageType = 0 };
        var stored = new InventoryItem { Id = 6, ItemId = 70000334, Count = 1, Slot = 2, StorageType = 2 };
        var owned = new List<InventoryItem> { stored, box };

        Assert.Same(box, RandomItemBattingHandler.FindBox(owned, items, 23, 362));
        Assert.Null(RandomItemBattingHandler.FindBox(owned, items, 36, 362));  
        Assert.Null(RandomItemBattingHandler.FindBox(owned, items, 59, 1));    
        Assert.Null(RandomItemBattingHandler.FindBox(owned, items, 23, 999));
    }

    
    
    
    [Fact]
    public void AnsRandomItemInBoxList_2713_ReaderLayout()
    {
        var body = RandomBoxPackets.Build2713([70000362u, 60000408u]);
        Assert.Equal(4 + 9 + 2 * RandomBoxPackets.InBoxListElementBytes, body.Length);
        Assert.Equal(body.Length - 4, (int)BinaryPrimitives.ReadUInt32LittleEndian(body));
        Assert.Equal("19000000" + "0000" + "0000000000" + "0200" + "ea1e2c04" + "00000000" + "98889303" + "00000000",
            Convert.ToHexString(body).ToLowerInvariant());

        var empty = RandomBoxPackets.Build2713([]);
        Assert.Equal("09000000" + "0000" + "0000000000" + "0000", Convert.ToHexString(empty).ToLowerInvariant());
    }

    [Fact]
    public void RandomItemBatting_InBoxList_IsTheDistinctItemsOfTheDrawableRows()
    {
        var rows = new List<RO2.Domain.GameData.Tables.RandomSet>
        {
            new() { ID = 804, Item = 70000362, Package = 1, Ratio = 10, NationEnable = 1 },
            new() { ID = 804, Item = 60000408, Package = 1, Ratio = 5, NationEnable = 1 },
            new() { ID = 804, Item = 70000362, Package = 5, Ratio = 1, NationEnable = 1 }, 
        };
        Assert.Equal([70000362u, 60000408u], RandomItemBattingHandler.InBoxListItems(rows));
    }
    
    
    [Fact]
    public void AnsPCollectionUsePetEgg_2925_RefusalsMatchDumpFrames()
    {
        
        Assert.Equal("cb7af204003175", Convert.ToHexString(CollectionPackets.BuildAnsPCollectionUsePetEgg(
            83000011u, 0, PCollectionUsePetEggHandler.ResultLoyaltyCapped)).ToLowerInvariant());
        
        Assert.Equal("d37af204003475", Convert.ToHexString(CollectionPackets.BuildAnsPCollectionUsePetEgg(
            83000019u, 0, PCollectionUsePetEggHandler.ResultNoEgg)).ToLowerInvariant());
    }
}
