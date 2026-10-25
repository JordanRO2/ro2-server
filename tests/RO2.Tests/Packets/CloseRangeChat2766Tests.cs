using RO2.Domain.Entities;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using RO2.GameData.Tables;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Chat;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// 2766 NfyNewMasterCloseRangeChatRoom and the close-range room master hand-off: when the owner leaves a room
/// that others are still in, the room passes to another member instead of closing.
/// </summary>
public class CloseRangeChat2766Tests
{
 /// <summary>The room GUID, which is kept unchanged across master hand-offs.</summary>
    private const string RefGuid = "5808aa1f2dc6b64aacc4c25e3da300ec";

    
    [Theory]
    
    [InlineData(0x0D851411u, "028300830065e41114850d6e0001f80000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001114850d5808aa1f2dc6b64aacc4c25e3da300ec")]
    
    [InlineData(0x0D785D11u, "028300830065e4115d780d6e0001f8000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000115d780d5808aa1f2dc6b64aacc4c25e3da300ec")]
    public void Builder_ReproducesTheReferenceHandOffs(uint newMaster, string retail)
    {
        
        var name = new byte[CloseRangeChatPackets.NameBufferChars * 2];
        name[0] = 0x6E;
        name[2] = 0x01;
        name[3] = 0xF8;

        var body = CloseRangeChatPackets.BuildNfyNewMasterCloseRangeChatRoom2766(newMaster, name, hasPassword: false,
            Convert.FromHexString(RefGuid));

        Assert.Equal(retail, Hex(body));
        Assert.Equal(134, body.Length);
    }

    [Fact]
    public void Builder_ReproducesTheLiveHandOff_FromTheRoomsOwn2763()
    {
        
        
        
        var sign2763 = Convert.FromHexString(
            "028400840062e4113a3903430068006100740052006f006f006d00320001f80000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000113a39030ae2d2ac6e2100dd49a614d441e4af75c3");
        var blob = sign2763.AsSpan(3);
        var name = blob.Slice(8, 102);
        var guid = blob.Slice(116, 16);
        Assert.Equal(sign2763, CloseRangeChatPackets.BuildNfyOpenCloseRangeChatRoom2763(0x03393A11u, name, 10, guid));

        var body = CloseRangeChatPackets.BuildNfyNewMasterCloseRangeChatRoom2766(0x0338DD11u, name, hasPassword: false, guid);

        Assert.Equal("028300830065e411dd3803430068006100740052006f006f006d00320001f8000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000011dd3803e2d2ac6e2100dd49a614d441e4af75c3",
            Hex(body));
    }

    [Fact]
    public void Builder_PasswordFlag_IsTheSignsAsFor2763()
    {
        
        
        var name = new byte[102];
        var guid = new byte[16];
        Assert.Equal(CloseRangeChatPackets.SignNoPassword,
            CloseRangeChatPackets.BuildNfyNewMasterCloseRangeChatRoom2766(1, name, false, guid)[3 + 110]);
        Assert.Equal(CloseRangeChatPackets.SignHasPassword,
            CloseRangeChatPackets.BuildNfyNewMasterCloseRangeChatRoom2766(1, name, true, guid)[3 + 110]);
        Assert.Throws<ArgumentException>(() => CloseRangeChatPackets.BuildNfyNewMasterCloseRangeChatRoom2766(1, name.AsSpan(0, 100), false, guid));
        Assert.Throws<ArgumentException>(() => CloseRangeChatPackets.BuildNfyNewMasterCloseRangeChatRoom2766(1, name, false, guid.AsSpan(0, 15)));
    }

    [Fact]
    public void HandOffBurst_IsRetailsOneMillisecond()
    {
        
        Assert.Equal("010c0c0064e4115d780d115d780d", Hex(CloseRangeChatPackets.BuildNfyExitCloseRangeChatRoom2765(0x0D785D11u)));
        Assert.Equal("010e0e0067e41114850d1114850d0a01", Hex(CloseRangeChatPackets.BuildNfyUpdateJoinMemberCount2770(0x0D851411u, 10, 1)));
        
        Assert.Equal("010c0c0063e4115d780d115d780d", Hex(CloseRangeChatPackets.BuildNfyJoinCloseRangeChatRoom2764(0x0D785D11u)));
        Assert.Equal("010e0e0067e41114850d1114850d0a02", Hex(CloseRangeChatPackets.BuildNfyUpdateJoinMemberCount2770(0x0D851411u, 10, 2)));
        
        Assert.Equal("010e0e0067e411dd380311dd38030a01", Hex(CloseRangeChatPackets.BuildNfyUpdateJoinMemberCount2770(0x0338DD11u, 10, 1)));
    }

    
    [Fact]
    public void Registry_FollowsTheReferenceRoomThroughItsThreeHandOffs()
    {
        
        
        var guid = Guid.NewGuid().ToByteArray();
        uint a = 0xC0000000u | (uint)Random.Shared.Next(1, 0x3FFFFFFF), b = a + 1;
        var name = new byte[102];
        name[0] = 0x6E;
        CloseRangeChatRooms.Register(new CloseRangeRoom(CloseRangeChatRooms.IdOf(guid), a, 0x0D785D11u, 10, name, false));

        
        var first = CloseRangeChatRooms.ResolveExit(guid, a, b, 0x0D851411u);
        Assert.True(first is CloseRangeMasterHandedOff
        {
            OldRoom.OwnerHandle: 0x0D785D11u,
            NewRoom: { OwnerCharacterId: var owner1, OwnerHandle: 0x0D851411u, Capacity: 10, HasPassword: false },
        } && owner1 == b);
        Assert.Same(name, CloseRangeChatRooms.Find(guid)?.NameBuffer);

        
        Assert.True(CloseRangeChatRooms.ResolveExit(guid, a) is CloseRangeMemberExited { Room.OwnerHandle: 0x0D851411u });

        
        Assert.True(CloseRangeChatRooms.ResolveExit(guid, b, a, 0x0D785D11u) is CloseRangeMasterHandedOff
        {
            NewRoom.OwnerHandle: 0x0D785D11u,
        });

        
        Assert.True(CloseRangeChatRooms.ResolveExit(guid, a, b, 0x0D851411u) is CloseRangeMasterHandedOff
        {
            OldRoom.OwnerHandle: 0x0D785D11u,
            NewRoom.OwnerHandle: 0x0D851411u,
        });
        Assert.Equal(CloseRangeChatRooms.IdOf(guid), CloseRangeChatRooms.Find(guid)?.Id);

        
        Assert.True(CloseRangeChatRooms.ResolveExit(guid, b) is CloseRangeOwnerExited { Room.OwnerHandle: 0x0D851411u });
        Assert.Null(CloseRangeChatRooms.Find(guid));
        Assert.True(CloseRangeChatRooms.ResolveExit(guid, b, a, 1) is CloseRangeUnknownRoom);
    }

    
    [Fact]
    public async Task Chat_OwnerExit_WithMembersLeft_HandsTheRoomToTheLowestUid()
    {
        await using var net = new Loopback();
        var u = NewUids(3);
        ulong ownerUid = u[2], memberUid = u[0], laterUid = u[1];
        var owner = await net.ConnectAsync(ownerUid, "Owner");
        var member = await net.ConnectAsync(memberUid, "Member");
        var later = await net.ConnectAsync(laterUid, "Later");
        var guid = Guid.NewGuid().ToByteArray();
        CloseRangeChatRoomMembers.Open(new CloseRangeChatRoomMembers.OpenedRoom(guid, 10, []), MemberOf(owner, ownerUid));
        var sends = new List<CloseRangeChatRoomMembers.Send>();
        Assert.Equal((CloseRangeChatRoomMembers.ResultJoined, 2),
            CloseRangeChatRoomMembers.Join(guid, MemberOf(member, memberUid), [], sends));

        sends.Clear();
        var departure = CloseRangeChatRoomMembers.Leave(guid, (uint)ownerUid, sends);

        Assert.Equal(new CloseRangeChatRoomMembers.Departure(CloseRangeChatRoomMembers.LeaveOutcome.HandedOff, 1, (uint)memberUid),
            departure);
        
        var notice = Assert.Single(sends);
        Assert.Same(member, notice.Session);
        Assert.Equal(ChatS2COpcodes.NfyChatRoomExitMmeberInfo, notice.Opcode);
        Assert.Equal(CloseRangeChatRoomMembers.BuildExitMemberInfo23520(guid, ownerUid), notice.Body);
        Assert.True(CloseRangeChatRoomMembers.IsOpen(guid));

        
        sends.Clear();
        Assert.Equal((CloseRangeChatRoomMembers.ResultJoined, 2),
            CloseRangeChatRoomMembers.Join(guid, MemberOf(later, laterUid), [], sends));
        var list = sends.Single(s => s.Opcode == ChatS2COpcodes.AnsPrivateRoomInMember);
        Assert.Equal(CloseRangeChatRoomMembers.BuildMemberList23517(guid, memberUid, 10,
            [(memberUid, "Member"), (laterUid, "Later")]), list.Body);

        
        Assert.Equal(CloseRangeChatRoomMembers.LeaveOutcome.NotAMember,
            CloseRangeChatRoomMembers.Leave(guid, (uint)ownerUid, []).Outcome);
    }

    [Fact]
    public async Task Chat_OwnerAloneLeaves_ClosesTheRoom()
    {
        await using var net = new Loopback();
        var u = NewUids(2);
        var owner = await net.ConnectAsync(u[0], "Owner");
        var member = await net.ConnectAsync(u[1], "Member");
        var guid = Guid.NewGuid().ToByteArray();
        CloseRangeChatRoomMembers.Open(new CloseRangeChatRoomMembers.OpenedRoom(guid, 10, []), MemberOf(owner, u[0]));
        CloseRangeChatRoomMembers.Join(guid, MemberOf(member, u[1]), [], []);

        
        
        Assert.Equal(new CloseRangeChatRoomMembers.Departure(CloseRangeChatRoomMembers.LeaveOutcome.MemberLeft, 1, 0),
            CloseRangeChatRoomMembers.Leave(guid, (uint)u[1], []));
        var sends = new List<CloseRangeChatRoomMembers.Send>();
        Assert.Equal(new CloseRangeChatRoomMembers.Departure(CloseRangeChatRoomMembers.LeaveOutcome.OwnerLeft, 0, 0),
            CloseRangeChatRoomMembers.Leave(guid, (uint)u[0], sends));
        Assert.Empty(sends);
        Assert.False(CloseRangeChatRoomMembers.IsOpen(guid));
    }

    [Fact]
    public async Task Chat_OwnerDisconnect_HandsOffAtOnce_AndReportsItForTheZone()
    {
        await using var net = new Loopback();
        var u = NewUids(3);
        ulong ownerUid = u[1], lowUid = u[0], highUid = u[2];
        var owner = await net.ConnectAsync(ownerUid, "Owner");
        var low = await net.ConnectAsync(lowUid, "Low");
        var high = await net.ConnectAsync(highUid, "High");
        var guid = Guid.NewGuid().ToByteArray();
        CloseRangeChatRoomMembers.Open(new CloseRangeChatRoomMembers.OpenedRoom(guid, 10, []), MemberOf(owner, ownerUid));
        CloseRangeChatRoomMembers.Join(guid, MemberOf(low, lowUid), [], []);
        CloseRangeChatRoomMembers.Join(guid, MemberOf(high, highUid), [], []);

        
        var sends = new List<CloseRangeChatRoomMembers.Send>();
        Assert.Empty(CloseRangeChatRoomMembers.OnSessionClosed(high, sends));
        Assert.Empty(sends);

        
        
        var handOff = Assert.Single(CloseRangeChatRoomMembers.OnSessionClosed(owner, sends));
        Assert.Equal(guid, handOff.RoomGuid);
        Assert.Equal((uint)ownerUid, handOff.OldMasterCharacterId);
        Assert.Equal((uint)lowUid, handOff.NewMasterCharacterId);
        Assert.Equal(1, handOff.MemberCount);
        var notice = Assert.Single(sends);
        Assert.Same(low, notice.Session);
        Assert.Equal(CloseRangeChatRoomMembers.BuildExitMemberInfo23520(guid, ownerUid), notice.Body);

        
        Assert.Equal(CloseRangeChatRoomMembers.LeaveOutcome.OwnerLeft,
            CloseRangeChatRoomMembers.Leave(guid, (uint)lowUid, []).Outcome);
    }

    
    [Fact]
    public void Signals_CurrentOwner_FollowsHandOffs_AndClosesOnlyOnTheOwnersExit()
    {
        var guid = Guid.NewGuid().ToByteArray();
        const uint a = 11, b = 12;
        int t = 1_700_000_000;
        var open = Signal(t, CloseRangeRoomSignals.OpenKind, guid, a);

        
        
        var handAB = Signal(++t, CloseRangeRoomSignals.ExitKind, guid, a, newMaster: b);
        var memberExitA = Signal(++t, CloseRangeRoomSignals.ExitKind, guid, a);
        var handBA = Signal(++t, CloseRangeRoomSignals.ExitKind, guid, b, newMaster: a);
        var goneAB = Signal(++t, CloseRangeRoomSignals.ExitKind, guid, a, newMaster: b, ownerGone: true);
        Assert.Equal(b, CloseRangeRoomSignals.CurrentOwner(open, [handAB]));
        Assert.Equal(b, CloseRangeRoomSignals.CurrentOwner(open, [handAB, memberExitA]));
        Assert.Equal(b, CloseRangeRoomSignals.CurrentOwner(open, [handAB, handBA, goneAB]));

        
        var otherRoomOfA = Signal(++t, CloseRangeRoomSignals.OpenKind, Guid.NewGuid().ToByteArray(), a);
        Assert.Equal(b, CloseRangeRoomSignals.CurrentOwner(open, [handAB, otherRoomOfA]));

        
        var closeB = Signal(++t, CloseRangeRoomSignals.ExitKind, guid, b);
        Assert.Null(CloseRangeRoomSignals.CurrentOwner(open, [handAB, handBA, goneAB, closeB]));
        var otherRoomOfB = Signal(++t, CloseRangeRoomSignals.OpenKind, Guid.NewGuid().ToByteArray(), b);
        Assert.Null(CloseRangeRoomSignals.CurrentOwner(open, [handAB, otherRoomOfB]));
        Assert.Null(CloseRangeRoomSignals.CurrentOwner(open, [Signal(++t, CloseRangeRoomSignals.ExitKind, guid, a)]));
        Assert.Equal(a, CloseRangeRoomSignals.CurrentOwner(open, []));
    }

    
    private static CloseRangeRoomSignal Signal(int timestamp, string kind, byte[] guid, uint character,
        uint newMaster = 0, bool ownerGone = false) => new()
    {
        Id = ObjectId.GenerateNewId(timestamp).ToString(),
        Kind = kind,
        RoomGuid = guid,
        CharacterId = character,
        NewMasterCharacterId = newMaster,
        OwnerGone = ownerGone,
    };

    private static CloseRangeChatRoomMembers.Member MemberOf(ProudSession session, ulong uid)
        => new(session, (uint)uid, uid, session.Username!);

 /// <summary>Distinct random uids, ascending, whose low 32 bits are also distinct (they double as character ids).</summary>
    private static ulong[] NewUids(int n)
    {
        ulong band = 0x0D00000000000000UL | ((ulong)Random.Shared.NextInt64(1, 0xFFFFFF) << 24);
        var uids = new ulong[n];
        for (int i = 0; i < n; i++) uids[i] = band + (ulong)(i + 1);
        return uids;
    }

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

 /// <summary>Real connected <see cref="ProudSession"/>s on the accepting end of loopback sockets.</summary>
    private sealed class Loopback : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<Socket> _clients = [];
        private readonly List<ProudSession> _sessions = [];
        private uint _nextId = 27660;

        public Loopback() => _listener.Start();

        public async Task<ProudSession> ConnectAsync(ulong uid, string name)
        {
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var accept = _listener.AcceptSocketAsync();
            await client.ConnectAsync((IPEndPoint)_listener.LocalEndpoint);
            var accepted = await accept;
            var session = new ProudSession(accepted, ++_nextId, NullLogger.Instance) { Username = name, CharacterId = (uint)uid };
            MessengerUid.Set(session, uid);
            _clients.Add(client);
            _sessions.Add(session);
            return session;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var s in _sessions) await s.DisposeAsync();
            foreach (var c in _clients) c.Dispose();
            _listener.Stop();
        }
    }
}

/// <summary>
/// 2858 NfyDeleteAcceptedKhara: removal of accepted Khara during the profession-change burst (KharaService,
/// ChangeProJobIDHandler), tested against the shipped data/Khara_Info.json.
/// </summary>
public class NfyDeleteAcceptedKhara2858Tests
{
    private static readonly Lazy<KharaCatalog> Catalog = new(() => new KharaCatalog(LoadRows()));

    [Fact]
    public void EmptyList_IsRetailsBody()
    {
        
        Assert.Equal("020000000000", Convert.ToHexString(KharaService.BuildNfyDeleteAcceptedKhara2858([])).ToLowerInvariant());
    }

    [Fact]
    public void NonEmptyList_FollowsTheClientsReader()
    {
        
        Assert.Equal("0a0000000200b8000000ba000000",
            Convert.ToHexString(KharaService.BuildNfyDeleteAcceptedKhara2858([184, 186])).ToLowerInvariant());
    }

    [Fact]
    public void ProfessionBoundKharas_AreTheFourOfKharaInfo()
    {
        
        var bound = LoadRows().Where(r => r.PC_ProJob != 0).Select(r => (r.Quest_ID, r.PC_ProJob)).ToArray();
        Assert.Equal(new (ushort, byte)[] { (184, 3), (185, 4), (186, 2), (187, 1) }, bound);
    }

    [Theory]
    [InlineData((byte)4, new uint[] { 184, 186, 187 })]
    [InlineData((byte)3, new uint[] { 185, 186, 187 })]
    [InlineData((byte)2, new uint[] { 184, 185, 187 })]
    public void OtherProfessionKharas_KeepTheNewProfessionsAndUnboundOnes(byte profession, uint[] expected)
    {
        
        var state = new KharaStateDocument { Id = 1 };
        foreach (var id in new uint[] { 184, 185, 186, 187, 343 })
            state.Active.Add(new KharaActiveDocument { KharaId = id, Counts = [0] });
        state.Completed.AddRange([184, 185]);

        Assert.Equal(expected, KharaService.OtherProfessionKharas(state, Catalog.Value, profession));
        
        var own = new KharaStateDocument { Id = 2, Active = [new KharaActiveDocument { KharaId = 185, Counts = [3] }] };
        Assert.Empty(KharaService.OtherProfessionKharas(own, Catalog.Value, 4));
    }

    
    private static readonly JsonSerializerOptions DataJsonOptions =
        new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };

    private static List<KharaInfo> LoadRows()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "data", "Khara_Info.json")))
            dir = dir.Parent;
        if (dir == null)
            throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/Khara_Info.json");
        return JsonSerializer.Deserialize<List<KharaInfo>>(
            File.ReadAllText(Path.Combine(dir.FullName, "data", "Khara_Info.json")), DataJsonOptions)!;
    }
}
