using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// 1389 ReqPartyNotice, 1390 ReqPartyRight, 1391 ReqPartyChangeGroup and the 1572 kick-vote start with its
/// 2826/2829 answers. These layouts are derived from the message definitions.
/// </summary>
public class PartyP3A1Tests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    // ---- 1391 ReqPartyChangeGroup: blob 26, dword 0xE32D001A at blob+0. ----

    private static byte[] Body1391(uint handle, ulong gidA, byte grpA, ulong gidB, byte grpB)
    {
        var blob = new byte[26];
        BinaryPrimitives.WriteUInt32LittleEndian(blob, 0xE32D001Au); // innerSize 26, msgTag 58157
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(4), handle);
        BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(8), gidA);
        blob[16] = grpA;
        BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(17), gidB);
        blob[25] = grpB;
        return [0x01, 26, .. blob];
    }

    [Fact]
    public void ChangeGroup1391_ParsesTheBuildersBlob()
    {
        var body = Body1391(0x0B69AA11u, 0x0DE0B6B3A7000001UL, 2, 0x0DE0B6B3A7000002UL, 3);
        Assert.Equal(
            "011a1a002de311aa690b010000a7b3b6e00d02020000a7b3b6e00d03",
            Convert.ToHexString(body).ToLowerInvariant());
        Assert.True(PartyChangeGroupHandler.TryParse(body, out var r));
        Assert.Equal(new PartyChangeGroupRequest(0x0B69AA11u, 0x0DE0B6B3A7000001UL, 2, 0x0DE0B6B3A7000002UL, 3), r);

        // A wrong msgTag (1390's) or a short blob is refused.
        var wrongTag = (byte[])body.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(wrongTag.AsSpan(4), 58155);
        Assert.False(PartyChangeGroupHandler.TryParse(wrongTag, out _));
        Assert.False(PartyChangeGroupHandler.TryParse(body.AsSpan(0, 20), out _));
    }

    [Fact]
    public void ChangeGroup1391_LeaderOnly_AndBothGidsMustBeMembers()
    {
        var (parties, leader, member) = TwoMemberParty();
        var gidL = PartyAnswerPackets.MemberGid(leader);
        var gidM = PartyAnswerPackets.MemberGid(member);

        Assert.True(parties.ChangeGroup(leader.SessionId, gidL, 1, gidM, 0));
        Assert.False(parties.ChangeGroup(member.SessionId, gidL, 1, gidM, 0)); // not the leader
        Assert.False(parties.ChangeGroup(leader.SessionId, gidL, 1, 0x0DE0B6B3A7FFFFFFUL, 0)); // B is no member
        Assert.False(parties.ChangeGroup(leader.SessionId, 0, 0, 0, 0));
        Assert.True(parties.ChangeGroup(leader.SessionId, gidM, 1, 0, 0)); // a zero record is allowed
        // No group state: the frame slots are untouched.
        var party = parties.GetPlayerParty(leader.SessionId)!;
        Assert.Equal(1, PartyService.GetFrameSlot(party, leader.SessionId));
        Assert.Equal(2, PartyService.GetFrameSlot(party, member.SessionId));
    }

    // ---- 1389 ReqPartyNotice: blob 2n+11, msgTag 58154 at blob+2, n at blob+8, text at +9. ----

    private static byte[] Body1389(uint handle, string textWithNul)
    {
        int n = textWithNul.Length;
        var blob = new byte[2 * n + 11];
        BinaryPrimitives.WriteUInt16LittleEndian(blob, (ushort)blob.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(2), 58154);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(4), handle);
        blob[8] = (byte)n;
        Encoding.Unicode.GetBytes(textWithNul).CopyTo(blob, 9); // the last 2 bytes stay unwritten (0)
        return [0x01, (byte)blob.Length, .. blob];
    }

    [Fact]
    public void Notice1389_ParsesTheBuildersBlob_AndCountsTheNul()
    {
        // The client sends n = wcslen + 1, so "Hi" is n = 3 and a 17-byte blob.
        var body = Body1389(0x0B69AA11u, "Hi\0");
        Assert.Equal("011111002ae311aa690b034800690000000000",
            Convert.ToHexString(body).ToLowerInvariant());
        Assert.True(PartyNoticeHandler.TryParse(body, out var notice));
        Assert.Equal("Hi", notice);

        // A count past the text is refused, and so is one above the builder's cap of 160.
        var bad = (byte[])body.Clone();
        bad[2 + 8] = 9;
        Assert.False(PartyNoticeHandler.TryParse(bad, out _));
        bad[2 + 8] = 161;
        Assert.False(PartyNoticeHandler.TryParse(bad, out _));
    }

    [Fact]
    public void Notice1389_IsKeptForTheLeaderOnly()
    {
        var (parties, leader, member) = TwoMemberParty();
        Assert.False(parties.SetNotice(member.SessionId, "x"));
        Assert.True(parties.SetNotice(leader.SessionId, "Meet at Prontera"));
        Assert.Equal("Meet at Prontera", parties.GetPlayerParty(leader.SessionId)!.Notice);
    }

    // ---- 1390 ReqPartyRight: blob 13, dword 0xE32B000D at blob+0. ----

    [Fact]
    public void Right1390_ParsesTheBuildersBlob()
    {
        var body = Convert.FromHexString("010d0d002be311aa690b7856341201");
        Assert.True(PartyRightHandler.TryParse(body, out var subject, out var grant));
        Assert.Equal(0x12345678u, subject);
        Assert.Equal(1, grant);
        Assert.False(PartyRightHandler.TryParse(body.AsSpan(0, 14), out _, out _));

        var (parties, leader, member) = TwoMemberParty();
        Assert.True(parties.SetRight(leader.SessionId, subject, grant));
        Assert.False(parties.SetRight(member.SessionId, subject, grant));
    }

    // ---- 1572 / 2826 / 2829 ----

    [Fact]
    public void KickVotePackets_MatchTheClientsReaders()
    {
        // 2826: two U16 fields (result first, seconds second).
        Assert.Equal(new byte[] { 0, 0, 0, 0 },
            PartyKickVotePackets.BuildAnsKickVoteStart2826(PartyKickVotePackets.ResultAccepted, 0));
        Assert.Equal(new byte[] { 0x78, 0x3D, 0x5A, 0x00 },
            PartyKickVotePackets.BuildAnsKickVoteStart2826(PartyKickVotePackets.ResultCooldown, 90));
        // 2829: Blob<16>: u64 +0 (initiator) and u64 +8 (target).
        Assert.Equal(Convert.FromHexString("010000a7b3b6e00d020000a7b3b6e00d"),
            PartyKickVotePackets.BuildNfyKickVote2829(0x0DE0B6B3A7000001UL, 0x0DE0B6B3A7000002UL));
        // 1572: NONE, exactly 8 bytes.
        Assert.True(PartyMemberKickVoteStartHandler.TryParse(Convert.FromHexString("020000a7b3b6e00d"), out var gid));
        Assert.Equal(0x0DE0B6B3A7000002UL, gid);
        Assert.False(PartyMemberKickVoteStartHandler.TryParse(new byte[4], out _));
        Assert.False(PartyMemberKickVoteStartHandler.TryParse(new byte[9], out _));
    }

    /// <summary>
    /// End to end through a real ProudServer: a 1572 naming a GID that is no member gets no answer; the one
    /// naming the other member gets 2826 [0][0] to the initiator and 2829 [initiator][target] to both.
    /// </summary>
    [Fact]
    public async Task KickVoteStart1572_AnswersInitiatorThenNotifiesParty()
    {
        await using var server = new ProudServer("Test", NullLogger.Instance);
        await server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
        using var initiatorClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var targetClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await initiatorClient.ConnectAsync(server.LocalEndPoint!);
        await targetClient.ConnectAsync(server.LocalEndPoint!);
        var deadline = DateTime.UtcNow + Generous;
        while (server.SessionCount < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Equal(2, server.SessionCount);

        ProudSession SessionOf(Socket c) =>
            server.GetSessions().Single(s => s.RemoteEndPoint!.Port == ((IPEndPoint)c.LocalEndPoint!).Port);
        var initiatorSession = SessionOf(initiatorClient);
        var targetSession = SessionOf(targetClient);
        initiatorSession.Crypto.SetRc4Key(Rc4Key);
        initiatorSession.Handshake = HandshakeState.Connected; // RMIs are dispatched only after the handshake (S-H5)

        var players = new PlayerStateManager(new NoPersistence());
        var initiator = new PlayerState
            { SessionId = initiatorSession.SessionId, Name = "Voter", CharacterUid = 0x0DE0B6B3A7000001UL, EntityId = 0x0EFD3411u };
        var target = new PlayerState
            { SessionId = targetSession.SessionId, Name = "Target", CharacterUid = 0x0DE0B6B3A7000002UL, EntityId = 0x0A000011u };
        players.AddPlayer(initiator.SessionId, initiator);
        players.AddPlayer(target.SessionId, target);
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(server);
        var parties = new PartyService(players, broadcast, NullLogger<PartyService>.Instance);
        var party = new PartyState
        {
            PartyId = 0x13F8, LeaderSessionId = target.SessionId,
            MemberSessionIds = [target.SessionId, initiator.SessionId],
        };
        party.Members[initiator.SessionId] = initiator;
        party.Members[target.SessionId] = target;
        party.FrameSlots[target.SessionId] = 1;
        party.FrameSlots[initiator.SessionId] = 2;
        parties.Track(party);

        var handler = new PartyMemberKickVoteStartHandler(
            new ServiceCollection().AddSingleton(parties).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PartyMemberKickVoteStartHandler>.Instance, players, broadcast);
        server.PacketReceived += (s, id, payload) => id == handler.Opcode ? handler.HandleAsync(s, payload) : Task.CompletedTask;

        // 1. No member has this GID: nothing is sent (else it would be the first frame read below).
        await initiatorClient.SendAsync(Encrypted(Request(1, 1572, Convert.FromHexString("ffffffa7b3b6e00d"))), SocketFlags.None);
        // 2. The other member.
        await initiatorClient.SendAsync(Encrypted(Request(2, 1572, Convert.FromHexString("020000a7b3b6e00d"))), SocketFlags.None);

        var nfy = Convert.FromHexString("010000a7b3b6e00d020000a7b3b6e00d");
        var (op, body) = await ReadInternalRmiAsync(initiatorClient);
        Assert.Equal(2826, op);
        Assert.Equal(new byte[4], body);
        (op, body) = await ReadInternalRmiAsync(initiatorClient);
        Assert.Equal(2829, op);
        Assert.Equal(nfy, body);
        (op, body) = await ReadInternalRmiAsync(targetClient);
        Assert.Equal(2829, op);
        Assert.Equal(nfy, body);
    }

    // ------------------------------------------------------------------------------------------

    private static (PartyService Parties, PlayerState Leader, PlayerState Member) TwoMemberParty()
    {
        var players = new PlayerStateManager(new NoPersistence());
        var leader = new PlayerState { SessionId = 101, Name = "Leader", CharacterUid = 0x0DE0B6B3A7000001UL };
        var member = new PlayerState { SessionId = 102, Name = "Member", CharacterUid = 0x0DE0B6B3A7000002UL };
        players.AddPlayer(leader.SessionId, leader);
        players.AddPlayer(member.SessionId, member);
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        var parties = new PartyService(players, broadcast, NullLogger<PartyService>.Instance);
        var party = new PartyState
        {
            PartyId = 7, LeaderSessionId = leader.SessionId,
            MemberSessionIds = [leader.SessionId, member.SessionId],
        };
        party.Members[leader.SessionId] = leader;
        party.Members[member.SessionId] = member;
        party.FrameSlots[leader.SessionId] = 1;
        party.FrameSlots[member.SessionId] = 2;
        parties.Track(party);
        return (parties, leader, member);
    }

    /// <summary>A 64-byte test key (512-bit RC4, as the handshake installs).</summary>
    private static readonly byte[] Rc4Key = Enumerable.Range(0, 64).Select(i => (byte)(i * 37 + 11)).ToArray();

    /// <summary>The plaintext of a counter-bearing RMI message: [u16 counter][0x01][u16 rmiId][body].</summary>
    private static byte[] Request(ushort counter, ushort rmiId, byte[] body)
    {
        var plain = new byte[5 + body.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(plain, counter);
        plain[2] = 0x01;
        BinaryPrimitives.WriteUInt16LittleEndian(plain.AsSpan(3), rmiId);
        body.CopyTo(plain, 5);
        return plain;
    }

    /// <summary>[0x25][02 = RC4][CompactLen n][RC4(plaintext)], framed.</summary>
    private static byte[] Encrypted(byte[] plaintext)
    {
        using var crypto = new ProudNetCrypto();
        crypto.SetRc4Key(Rc4Key);
        var cipher = crypto.Rc4Decrypt(plaintext)!; // symmetric: re-keyed per message
        var header = cipher.Length < 128
            ? new byte[] { 0x25, 0x02, 0x01, (byte)cipher.Length }
            : new byte[] { 0x25, 0x02, 0x02, (byte)cipher.Length, (byte)(cipher.Length >> 8) };
        return ProudFrameCodec.EncodeFrame([.. header, .. cipher]);
    }

    /// <summary>One S2C [13 57][CompactLen n][0x01][u16 rmiId][body] off a client socket.</summary>
    private static async Task<(ushort RmiId, byte[] Body)> ReadInternalRmiAsync(Socket client)
    {
        var head = await ReadExactAsync(client, 3);
        Assert.Equal(new byte[] { 0x13, 0x57 }, head[..2]);
        var lenBytes = await ReadExactAsync(client, head[2]);
        int len = head[2] switch
        {
            1 => lenBytes[0],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(lenBytes),
            _ => BinaryPrimitives.ReadInt32LittleEndian(lenBytes),
        };
        var data = await ReadExactAsync(client, len);
        Assert.Equal(0x01, data[0]);
        return (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(1)), data[3..]);

        static async Task<byte[]> ReadExactAsync(Socket s, int n)
        {
            var buf = new byte[n];
            for (int got = 0; got < n;)
            {
                int r = await s.ReceiveAsync(buf.AsMemory(got), SocketFlags.None).AsTask().WaitAsync(Generous);
                if (r == 0) throw new EndOfStreamException();
                got += r;
            }
            return buf;
        }
    }

    private sealed class NoPersistence : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }
}
