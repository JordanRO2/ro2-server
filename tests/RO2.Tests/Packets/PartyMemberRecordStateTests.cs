using System.Buffers.Binary;
using System.Text;
using RO2.Domain.Entities;
using RO2.Network.Packets;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Member-state bytes of the shared 208-byte party record (2357/2359/2361): the online flag at rec+53, the
/// profession at rec+18, the ProJob pair at rec+64/65, the guild name at rec+108, and the class at rec+16 from the
/// same helper used by the 2310 and the character list.
/// </summary>
public class PartyMemberRecordStateTests
{
    
    
    
    private const string Retail2359 = "02dc00dc0033e300000000156902001164fc06ac9cd4a7b3b6e00d00000030060004c41b0000c41b0000d1050000d1050000bb040000000035d9294407c1f5444627d1410105000000414051060100000062006f0072006e003200770069006e0000000000000000000000000000000000000000000000000000004e0065006d0065007300690073002700000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";

 /// <summary>Body offset of record byte 0 in a 2359: 3-byte compact prefix, u16 innerSize, u16 msgTag, 8-byte header.</summary>
    private const int Join2359RecordStart = 3 + 4 + PartyNotifyPackets.JoinHeaderLen;

 /// <summary>rec+54..61: no source identified; sent as 0.</summary>
    private const int UnidentifiedStart = 54, UnidentifiedLen = 8;

    
    
    
    private const string CreateBlockProfession4 = "02010703ff00050000000603ff00010000000001000400";

    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }

    private static PartyService NewPartyService(PlayerStateManager players)
        => new(players,
            new BroadcastService(players, Microsoft.Extensions.Logging.Abstractions.NullLogger<BroadcastService>.Instance),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PartyService>.Instance);

    private static Character CharacterWith(string createHex, byte job, uint id)
    {
        var appearance = CharacterSerializer.ParseCreateAppearance(new PacketReader(Convert.FromHexString(createHex)));
        return new Character { Id = id, Job = job, AppearanceJson = CharacterSerializer.ToJson(appearance) };
    }

    private static int _nextChar = 1_950_000_000;
    private static uint NewCharId() => (uint)Interlocked.Increment(ref _nextChar);

    [Fact]
    public void ApplyMemberState_WritesTheFourFieldsAndReplacesTheGuild()
    {
        var rec = PartyNotifyPackets.BuildMemberRecord(0x06FC6411u, 0x0DE0B6B3A7D49CACUL, "born2win", "OldGuild");

        PartyNotifyPackets.ApplyMemberState(rec, online: true, profession: 4, proJobLevel: 40, proJobId: 4, guildName: "HEVEN");

        Assert.Equal(1, rec[53]);
        Assert.Equal(4, rec[18]);
        Assert.Equal(40, rec[64]);
        Assert.Equal(4, rec[65]);
        Assert.Equal("HEVEN", Encoding.Unicode.GetString(rec, 108, 42).TrimEnd('\0'));
        Assert.Equal("born2win", Encoding.Unicode.GetString(rec, 66, 42).TrimEnd('\0'));

        PartyNotifyPackets.ApplyMemberState(rec, online: false, profession: 4, proJobLevel: 0, proJobId: 0, guildName: null);
        Assert.Equal(0, rec[53]);
        Assert.All(rec.AsSpan(108, 42).ToArray(), b => Assert.Equal(0, b));
    }

 /// Every byte of the 2359 record that the live path has a source for comes from PartyService.BuildJoinDeltaBody.
 /// Only rec+54..61, which have no known source, are sent as 0.
    [Fact]
    public async Task JoinDelta_2359_MatchesRetail_ExceptTheUnidentifiedBytes()
    {
        var retail = Convert.FromHexString(Retail2359);
        var players = new PlayerStateManager(new NoDirtySink());
        var service = NewPartyService(players);

        var charId = NewCharId();
        var (guild, refusal) = await GuildService.Instance.CreateAsync(charId, "born2win", 48, 6, "Nemesis'");
        Assert.Equal(GuildService.CreateRefusal.None, refusal);
        try
        {
            const uint session = 0x06FC64; 
            var joiner = new PlayerState
            {
                SessionId = session, CharacterId = charId, CharacterUid = 0x0DE0B6B3A7D49CACUL, Name = "born2win",
                Level = 48, Job = 6, Hp = 7108, MaxHp = 7108, Mp = 1489, MaxMp = 1489, MapId = 1211,
                X = BitConverter.Int32BitsToSingle(0x4429D935), Y = BitConverter.Int32BitsToSingle(0x44F5C107),
                Z = BitConverter.Int32BitsToSingle(0x41D12746),
                
                Character = CharacterWith(CreateBlockProfession4, job: 6, id: charId),
            };
            players.AddPlayer(session, joiner);
            var party = new PartyState { PartyId = 0x269, LeaderSessionId = session, MemberSessionIds = [session] };
            party.Members[session] = joiner;
            party.FrameSlots[session] = 1;

            var body = service.BuildJoinDeltaBody(party, joiner);

            Assert.Equal(retail.Length, body.Length);
            var expected = (byte[])retail.Clone();
            expected.AsSpan(Join2359RecordStart + UnidentifiedStart, UnidentifiedLen).Clear();
            Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(body));
        }
        finally
        {
            await GuildService.Instance.DisbandAsync(guild!);
        }
    }

 /// The roster marks each member online (rec+53 = 1) or locked (0); the client draws the lock icon for 0. The class
 /// and profession come from the stored create block, because a character that never changed job stores its
 /// profession in Character.Job, which is not the class.
    [Fact]
    public void Roster_2361_MarksOnlineAndLockedMembers_AndSendsClassAndProfession()
    {
        var players = new PlayerStateManager(new NoDirtySink());
        var service = NewPartyService(players);

        var online = new PlayerState
        {
            SessionId = 21, CharacterId = NewCharId(), CharacterUid = 0x0DE0B6B3A7000021UL, Name = "Here", Level = 30,
            Job = 4, MapId = 1100,
            
            Character = CharacterWith("02010703ff00050000000603ff00010000000010000400", job: 4, id: 1),
        };
        var gone = new PlayerState
        {
            SessionId = 22, CharacterId = NewCharId(), CharacterUid = 0x0DE0B6B3A7000022UL, Name = "Away", Level = 50,
            MapId = 4151,
        };
        players.AddPlayer(21, online); 
        var party = new PartyState { PartyId = 0x31, LeaderSessionId = 21, MemberSessionIds = [21, 22] };
        party.Members[21] = online;
        party.Members[22] = gone;
        party.FrameSlots[21] = 1;
        party.FrameSlots[22] = 2;

        var body = service.BuildRosterBody(party);
        int Rec(int i) => 7 + PartyNotifyPackets.UpdateHeaderLen + 208 * i;

        Assert.Equal(1, body[Rec(0) + PartyNotifyPackets.RecOnlineOff]);
        Assert.Equal(0, body[Rec(1) + PartyNotifyPackets.RecOnlineOff]);

        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(Rec(0) + 16)));   
        Assert.Equal(4, body[Rec(0) + PartyNotifyPackets.RecProfessionOff]);
        Assert.Equal(0, body[Rec(0) + PartyNotifyPackets.RecProJobLevelOff]);                
        Assert.Equal(0, body[Rec(0) + PartyNotifyPackets.RecProJobIdOff]);
        Assert.All(body.AsSpan(Rec(0) + PartyNotifyPackets.RecGuildOff, 42).ToArray(), b => Assert.Equal(0, b)); 
    }
}
