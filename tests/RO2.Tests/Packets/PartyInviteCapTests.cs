using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// 1382 ReqPartyInvitation into a party already at its member cap (PartyService.InviteToPartyAsync). The inviter is
/// refused at once with 2346 code 15707 ("Can't invite anymore.") and the target gets no 2347 dialog. The cap is
/// MaxMembers of the party type (5 normal, 20 raid) over the current members, and it is checked before the
/// target-in-a-party refusal.
/// </summary>
public class PartyInviteCapTests
{
    
    
    
    private const string MrGargantaRequest1382 =
        "013333001be311f730004d007200470061007200670061006e00740061000000000000000000000000000000000000000000000000";
    private const string MrGargantaRefusal2346 =
        "013535001ce311f730005b3d4d007200470061007200670061006e00740061000000000000000000000000000000000000000000000000";

    [Fact]
    public void FullPartyRefusal_2346_IsRetailsFrame()
    {
        var request = Convert.FromHexString(MrGargantaRequest1382);
        
        var handle = BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(6));
        var name = System.Text.Encoding.Unicode.GetString(request, 10, 42).TrimEnd('\0');
        Assert.Equal(0x0030F711u, handle);
        Assert.Equal("MrGarganta", name);
        Assert.Equal(0, request[52]);

        Assert.Equal(Convert.FromHexString(MrGargantaRefusal2346),
            PartyStatePackets.BuildOnPartyInvite(handle, 15707, name, request[52]));
    }

 /// A full 5/5 normal party: inviting a stranger or one of its own members is refused with 2346 code 15707. Nobody
 /// gets a dialog and no invitation is left open, so a later invitation of the same stranger opens its dialog normally.
    [Fact]
    public async Task FullNormalParty_IsRefusedAtOnce_WithNoDialogAndNoOpenInvitation()
    {
        await using var rig = await PartyWireRig.StartAsync(7);
        var members = Enumerable.Range(0, 5).Select(i => rig.AddPlayer(i, "Member" + i)).ToArray();
        var stranger = rig.AddPlayer(5, "Stranger");
        var other = rig.AddPlayer(6, "Other");
        var party = rig.TrackParty(0x51, members);
        await rig.DrainAsync();

        await rig.Party.InviteToPartyAsync(members[0], stranger, "stranger");
        await rig.Party.InviteToPartyAsync(members[0], members[3], "Member3");

        var leaderFrames = await rig.FramesAsync(0);
        Assert.Equal(new[]
        {
            PartyWireRig.Frame(ZoneS2COpcodes.OnPartyInvite,
                PartyStatePackets.BuildOnPartyInvite(rig.Handle(0), 15707, "stranger", 0)),
            PartyWireRig.Frame(ZoneS2COpcodes.OnPartyInvite,
                PartyStatePackets.BuildOnPartyInvite(rig.Handle(0), 15707, "Member3", 0)),
        }, leaderFrames);
        Assert.Empty(await rig.FramesAsync(5));
        Assert.Empty(await rig.FramesAsync(3));
        Assert.Equal(5, party.MemberSessionIds.Count);

        
        await rig.Party.InviteToPartyAsync(other, stranger, "Stranger");
        Assert.Empty(await rig.FramesAsync(6));
        Assert.Equal(
            new[] { PartyWireRig.Frame(ZoneS2COpcodes.OnPartyJoin,
                PartyStatePackets.BuildOnPartyJoin(rig.Handle(5), other.CharacterUid, 0, "Other")) },
            await rig.FramesAsync(5));
    }

 /// The cap follows the party type: five members of a raid (type 2, cap 20) still get the dialog with kind 2, and the
 /// inviter hears nothing until the answer. One short of a normal cap is not refused either.
    [Theory]
    [InlineData(2, 5)]
    [InlineData(0, 4)]
    public async Task PartyWithRoom_OpensTheDialog(byte partyType, int memberCount)
    {
        await using var rig = await PartyWireRig.StartAsync(memberCount + 1);
        var members = Enumerable.Range(0, memberCount).Select(i => rig.AddPlayer(i, "Member" + i)).ToArray();
        var target = rig.AddPlayer(memberCount, "Target");
        var party = rig.TrackParty(0x52, members);
        party.PartyType = partyType;
        await rig.DrainAsync();

        await rig.Party.InviteToPartyAsync(members[0], target, "Target");

        Assert.Empty(await rig.FramesAsync(0));
        Assert.Equal(
            new[] { PartyWireRig.Frame(ZoneS2COpcodes.OnPartyJoin,
                PartyStatePackets.BuildOnPartyJoin(rig.Handle(memberCount), members[0].CharacterUid, partyType, "Member0")) },
            await rig.FramesAsync(memberCount));
    }

 /// A 20/20 raid is refused with the raid's own type byte in the 2346.
    [Fact]
    public async Task FullRaid_IsRefusedWithTypeTwo()
    {
        await using var rig = await PartyWireRig.StartAsync(2);
        var leader = rig.AddPlayer(0, "Leader");
        var target = rig.AddPlayer(1, "Target");
        var party = rig.TrackParty(0x53, [leader]);
        party.PartyType = 2;
        
        for (uint key = 1000; key < 1019; key++)
        {
            party.MemberSessionIds.Add(key);
            party.FrameSlots[key] = (byte)(key - 998);
        }
        await rig.DrainAsync();

        await rig.Party.InviteToPartyAsync(leader, target, "Target");

        Assert.Equal(
            new[] { PartyWireRig.Frame(ZoneS2COpcodes.OnPartyInvite,
                PartyStatePackets.BuildOnPartyInvite(rig.Handle(0), 15707, "Target", 2)) },
            await rig.FramesAsync(0));
        Assert.Empty(await rig.FramesAsync(1));
    }
}

/// <summary>
/// A ProudServer with <c>n</c> connected clients and a PartyService over them, for the party tests that check what
/// each client is sent. Frames are read up to a 0x5A OnLevelUp marker sent to every client.
/// </summary>
internal sealed class PartyWireRig : IAsyncDisposable
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    private readonly ProudServer _server;
    private readonly Socket[] _clients;

    public ProudSession[] Sessions { get; }
    public PlayerStateManager Players { get; } = new(new NoDirtySink());
    public BroadcastService Broadcast { get; }
    public PartyService Party { get; }

    private PartyWireRig(ProudServer server, Socket[] clients, ProudSession[] sessions)
    {
        _server = server;
        _clients = clients;
        Sessions = sessions;
        Broadcast = new BroadcastService(Players, NullLogger<BroadcastService>.Instance);
        Broadcast.SetServer(server);
        Party = new PartyService(Players, Broadcast, NullLogger<PartyService>.Instance);
    }

    public static async Task<PartyWireRig> StartAsync(int n)
    {
        var server = new ProudServer("test", NullLogger.Instance);
        await server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
        var clients = new Socket[n];
        var sessions = new ProudSession[n];
        for (int i = 0; i < n; i++)
        {
            var connected = new TaskCompletionSource<ProudSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            Func<ProudSession, Task> onConnect = s => { connected.TrySetResult(s); return Task.CompletedTask; };
            server.SessionConnected += onConnect;
            clients[i] = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await clients[i].ConnectAsync(server.LocalEndPoint!);
            sessions[i] = await connected.Task.WaitAsync(Generous);
            server.SessionConnected -= onConnect;
        }
        return new PartyWireRig(server, clients, sessions);
    }

 /// <summary>The PC handle client <paramref name="i"/> holds for itself, the one the party frames echo.</summary>
    public uint Handle(int i) => PlayerStateManager.BuildActorHandle(Sessions[i].SessionId);

    public PlayerState AddPlayer(int i, string name, uint mapId = 1100)
    {
        var sid = Sessions[i].SessionId;
        var p = new PlayerState
        {
            SessionId = sid, EntityId = Handle(i), CharacterId = 100 + (uint)i,
            CharacterUid = 0x0DE0B6B3A7000000UL + 100 + (uint)i, Name = name, Level = 50, MapId = mapId,
        };
        Players.AddPlayer(sid, p);
        return p;
    }

 /// <summary>A party of <paramref name="members"/> (the first leads), registered with the service.</summary>
    public PartyState TrackParty(uint partyId, IReadOnlyList<PlayerState> members)
    {
        var party = new PartyState
        {
            PartyId = partyId, LeaderSessionId = members[0].SessionId, FounderGid = members[0].CharacterUid,
        };
        for (int i = 0; i < members.Count; i++)
        {
            party.MemberSessionIds.Add(members[i].SessionId);
            party.Members[members[i].SessionId] = members[i];
            party.FrameSlots[members[i].SessionId] = (byte)(i + 1);
        }
        Party.Track(party);
        return party;
    }

    public static string Frame(ushort opcode, byte[] body) => $"{opcode}:{Convert.ToHexString(body).ToLowerInvariant()}";

 /// <summary>Reads and drops everything every client has been sent so far.</summary>
    public async Task DrainAsync()
    {
        for (int i = 0; i < _clients.Length; i++)
            await FramesAsync(i);
    }

 /// <summary>"op:hex" of each RMI client <paramref name="i"/> was sent since the last read.</summary>
    public async Task<List<string>> FramesAsync(int i)
    {
        await Sessions[i].SendInternalRmiAsync(ZoneS2COpcodes.OnLevelUp, new byte[] { 0x5A });
        var frames = new List<string>();
        while (true)
        {
            var head = await ReadExactAsync(i, 3);
            Assert.Equal(new byte[] { 0x13, 0x57 }, head[..2]);
            var lengthBytes = await ReadExactAsync(i, head[2]);
            int length = head[2] switch
            {
                1 => lengthBytes[0],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes),
                _ => BinaryPrimitives.ReadInt32LittleEndian(lengthBytes),
            };
            var data = await ReadExactAsync(i, length);
            if (data.Length < 3 || data[0] != 0x01) continue;
            var op = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(1));
            var body = data[3..];
            if (op == ZoneS2COpcodes.OnLevelUp && body is [0x5A]) return frames;
            frames.Add(Frame(op, body));
        }
    }

    private async Task<byte[]> ReadExactAsync(int i, int count)
    {
        var buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await _clients[i].ReceiveAsync(buffer.AsMemory(read), SocketFlags.None).AsTask().WaitAsync(Generous);
            if (n == 0) throw new EndOfStreamException("connection closed mid-frame");
            read += n;
        }
        return buffer;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _clients) c.Dispose();
        await _server.StopAsync();
    }

    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }
}
