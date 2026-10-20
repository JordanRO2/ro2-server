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
/// 1524 ReqSkillMoveCancel and its answers (SkillMoveCancelHandler): 2746 to the caster, then 2747 naming the
/// caster, sent to the caster itself as well as to the players around it.
/// </summary>
public class SkillMoveCancel2747Tests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    // Test vector: a 1524 for one skill with isCurrentCast 0, and the 2746 and self 2747 that answer it.
    private const string Request1524 = "896c060000";
    private const string Answer2746 = "0000896c0600";
    private const string Self2747 = "0110100060e4000000001178760e896c0600";
    private const uint CasterHandle = 0x0E767811u;

    // Second vector: a 1524 with flag 1 for another skill, and the self 2747 below.
    private const string Archive2747 = "0110100060e400000000113f2c0ef14a0600";

    [Fact]
    public void SelfCopies_AreRetailsFrames()
    {
        Assert.Equal(Convert.FromHexString(Self2747), ActorStatePackets.BuildNfySkillMoveCancel(CasterHandle, 0x00066C89u));
        Assert.Equal(Convert.FromHexString(Archive2747), ActorStatePackets.BuildNfySkillMoveCancel(0x0E2C3F11u, 0x00064AF1u));
    }

    /// <summary>
    /// End to end through a real ProudServer: the caster's stream is 2746 then its own 2747, byte for byte the
    /// vectors above, and the onlooker on the same map gets the same 2747.
    /// </summary>
    [Fact]
    public async Task Cancel_SendsTheCasterItsOwn2747AfterIts2746_AndTheOnlookerTheSame()
    {
        await using var server = new ProudServer("Test", NullLogger.Instance);
        await server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
        using var casterClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var onlookerClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await casterClient.ConnectAsync(server.LocalEndPoint!);
        await onlookerClient.ConnectAsync(server.LocalEndPoint!);
        var deadline = DateTime.UtcNow + Generous;
        while (server.SessionCount < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Equal(2, server.SessionCount);

        ProudSession SessionOf(Socket c) =>
            server.GetSessions().Single(s => s.RemoteEndPoint!.Port == ((IPEndPoint)c.LocalEndPoint!).Port);
        var casterSession = SessionOf(casterClient);
        var onlookerSession = SessionOf(onlookerClient);
        casterSession.Crypto.SetRc4Key(Rc4Key);
        casterSession.Handshake = HandshakeState.Connected; // RMIs are dispatched only after the handshake (S-H5)

        var players = new PlayerStateManager(new NoPersistence());
        var caster = new PlayerState { SessionId = casterSession.SessionId, Name = "Caster", EntityId = CasterHandle };
        var onlooker = new PlayerState { SessionId = onlookerSession.SessionId, Name = "Onlooker", EntityId = 0x0E76C011u };
        players.AddPlayer(caster.SessionId, caster);
        players.AddPlayer(onlooker.SessionId, onlooker);
        // The 2747 broadcast needs the caster actively casting the cancelled skill (MV-9): an open cast bar.
        SkillCastLandings.BeginCasting(caster, 0x00066C89u, DateTime.UtcNow);
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(server);

        var handler = new SkillMoveCancelHandler(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SkillMoveCancelHandler>.Instance, players, broadcast);
        server.PacketReceived += (s, id, payload) => id == handler.Opcode ? handler.HandleAsync(s, payload) : Task.CompletedTask;

        await casterClient.SendAsync(Encrypted(Request(1, handler.Opcode, Convert.FromHexString(Request1524))), SocketFlags.None);

        var (op, body) = await ReadInternalRmiAsync(casterClient);
        Assert.Equal(2746, op);
        Assert.Equal(Convert.FromHexString(Answer2746), body);
        (op, body) = await ReadInternalRmiAsync(casterClient);
        Assert.Equal(2747, op);
        Assert.Equal(Convert.FromHexString(Self2747), body);

        (op, body) = await ReadInternalRmiAsync(onlookerClient);
        Assert.Equal(2747, op);
        Assert.Equal(Convert.FromHexString(Self2747), body);
    }

    // ------------------------------------------------------------------------------------------
    // Transport helpers, as in PartyKick2353Tests.

    private static readonly byte[] Rc4Key = Enumerable.Range(0, 64).Select(i => (byte)(i * 37 + 11)).ToArray();

    private static byte[] Request(ushort counter, ushort opcode, byte[] body)
    {
        var plain = new byte[5 + body.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(plain, counter);
        plain[2] = 0x01;
        BinaryPrimitives.WriteUInt16LittleEndian(plain.AsSpan(3), opcode);
        body.CopyTo(plain, 5);
        return plain;
    }

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
