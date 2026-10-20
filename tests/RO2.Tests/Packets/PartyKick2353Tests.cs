using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// 1388 ReqPartyExpulsion and its answer 2353 OnPartyExpulsion (PartyKickHandler): the 15712 refusal for a GID that is
/// no member, and the 2358-first order of a kick that disbands the party.
/// </summary>
public class PartyKick2353Tests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    // A 1388 naming a GID that is no member, and its only answer: 2353 with result 15712 "You are not Raid Member."
    // (string 9408).
    private const string NotMemberRequest1388 = "0110100028e311aa690b01dee1a7b3b6e00d";
    private const string NotMemberAnswer2353 = "010a0a0029e311aa690b603d";

    // A 1388 that expels the other member of a two-member party, disbanding it. The party frames it produces:
    private const string DisbandRequest1388 = "0110100028e31134fd0e56c7a6a7b3b6e00d";
    private const string Disband2358 = "010c0c0032e30000000015f81300";
    private const string Disband2353 = "010a0a0029e31134fd0e0000";
    private const string Disband2366 = "011414003ae30000000015f8130056c7a6a7b3b6e00d";
    private const uint DisbandPartyId = 0x13F8;
    private const ulong ExpelledGid = 0x0DE0B6B3A7A6C756UL;

    [Fact]
    public void NotMemberAnswer_IsRetailsFrame()
    {
        var request = Convert.FromHexString(NotMemberRequest1388);
        Assert.True(PartyAnswerPackets.TryGetCompactBlob(request, out var blob));
        Assert.True(PartyAnswerPackets.TryReadEnvelope(blob, PartyAnswerPackets.MsgTagReqPartyExpulsion, 16, out var handle));
        Assert.Equal(0x0B69AA11u, handle);

        Assert.Equal(Convert.FromHexString(NotMemberAnswer2353), PartyAnswerPackets.BuildResultAck(
            PartyAnswerPackets.MsgTagOnPartyExpulsion, handle, PartyAnswerPackets.ResultExpulsionNotMember));
        // The success form is the same blob with result 0.
        Assert.Equal(Convert.FromHexString(Disband2353),
            PartyAnswerPackets.BuildResultAck(PartyAnswerPackets.MsgTagOnPartyExpulsion, 0x0EFD3411u));
    }

    /// <summary>
    /// End to end through a real ProudServer. The kicker first names a GID that is no member (retail's 15712
    /// request): its only answer is retail's 2353 15712. Then it kicks the other member of a two-member party
    /// with retail's disbanding request: its next four frames are retail's 2358, 2353, 2366, 2358 byte for byte
    /// (the refusal sent nothing else, or it would come first), and the kicked player's first frame is its 2367.
    /// </summary>
    [Fact]
    public async Task RefusalThenDisbandingKick_SendRetailsFramesInRetailsOrder()
    {
        await using var server = new ProudServer("Test", NullLogger.Instance);
        await server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
        using var kickerClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var targetClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await kickerClient.ConnectAsync(server.LocalEndPoint!);
        await targetClient.ConnectAsync(server.LocalEndPoint!);
        var deadline = DateTime.UtcNow + Generous;
        while (server.SessionCount < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Equal(2, server.SessionCount);

        ProudSession SessionOf(Socket c) =>
            server.GetSessions().Single(s => s.RemoteEndPoint!.Port == ((IPEndPoint)c.LocalEndPoint!).Port);
        var kickerSession = SessionOf(kickerClient);
        var targetSession = SessionOf(targetClient);
        kickerSession.Crypto.SetRc4Key(Rc4Key);
        kickerSession.Handshake = HandshakeState.Connected; // RMIs are dispatched only after the handshake (S-H5)

        var players = new PlayerStateManager(new NoPersistence());
        var kicker = new PlayerState
            { SessionId = kickerSession.SessionId, Name = "Kicker", CharacterUid = 0x0DE0B6B3A7000001UL, EntityId = 0x0EFD3411u };
        var target = new PlayerState
            { SessionId = targetSession.SessionId, Name = "Kicked", CharacterUid = ExpelledGid, EntityId = 0x0A000011u };
        players.AddPlayer(kicker.SessionId, kicker);
        players.AddPlayer(target.SessionId, target);
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(server);
        var parties = new PartyService(players, broadcast, NullLogger<PartyService>.Instance);
        var party = new PartyState
        {
            PartyId = DisbandPartyId, LeaderSessionId = kicker.SessionId,
            MemberSessionIds = [kicker.SessionId, target.SessionId],
        };
        party.Members[kicker.SessionId] = kicker;
        party.Members[target.SessionId] = target;
        party.FrameSlots[kicker.SessionId] = 1;
        party.FrameSlots[target.SessionId] = 2;
        parties.Track(party);

        var handler = new PartyKickHandler(
            new ServiceCollection().AddSingleton(parties).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PartyKickHandler>.Instance, players, broadcast);
        server.PacketReceived += (s, id, payload) => id == handler.Opcode ? handler.HandleAsync(s, payload) : Task.CompletedTask;

        // 1. A GID that is no member: the refusal alone, and the party is untouched.
        await kickerClient.SendAsync(Encrypted(Request1388(1, Convert.FromHexString(NotMemberRequest1388))), SocketFlags.None);
        var (op, body) = await ReadInternalRmiAsync(kickerClient);
        Assert.Equal(2353, op);
        Assert.Equal(Convert.FromHexString(NotMemberAnswer2353), body);

        // 2. Retail's disbanding kick.
        await kickerClient.SendAsync(Encrypted(Request1388(2, Convert.FromHexString(DisbandRequest1388))), SocketFlags.None);
        var expected = new (ushort Op, string Body)[]
        {
            (2358, Disband2358), (2353, Disband2353), (2366, Disband2366), (2358, Disband2358),
        };
        foreach (var (wantOp, wantBody) in expected)
        {
            (op, body) = await ReadInternalRmiAsync(kickerClient);
            Assert.Equal(wantOp, op);
            Assert.Equal(Convert.FromHexString(wantBody), body);
        }

        (op, body) = await ReadInternalRmiAsync(targetClient);
        Assert.Equal(2367, op);
        Assert.Equal(PartyLayoffPackets.BuildPartyNotifyLayoff2367(), body);
        Assert.Null(parties.GetPlayerParty(kicker.SessionId)); // the party is gone
    }

    // ------------------------------------------------------------------------------------------
    // Transport helpers, as in Whisper2325NotFoundTests.

    /// <summary>A 64-byte test key (512-bit RC4, as the handshake installs).</summary>
    private static readonly byte[] Rc4Key = Enumerable.Range(0, 64).Select(i => (byte)(i * 37 + 11)).ToArray();

    /// <summary>The plaintext of a counter-bearing RMI message: [u16 counter][0x01][u16 1388][body].</summary>
    private static byte[] Request1388(ushort counter, byte[] body)
    {
        var plain = new byte[5 + body.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(plain, counter);
        plain[2] = 0x01;
        BinaryPrimitives.WriteUInt16LittleEndian(plain.AsSpan(3), 1388);
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
