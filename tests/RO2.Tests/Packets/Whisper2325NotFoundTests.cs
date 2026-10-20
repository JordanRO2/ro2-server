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
/// 2325 OnReceiveWhisper for a recipient who cannot be reached: one answer, to the sender only, with result
/// code 15703 at blob+8 (<see cref="WhisperHandler.TargetNotFoundResult"/>). The answer is the only reply to
/// the 1316 and carries the sender's own handle, the recipient name as typed and the message.
/// </summary>
public class Whisper2325NotFoundTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    // Vector: a sender whispers "ur welcome" to an offline recipient: the 1316 request body and the 2325 answer
    // body.
    private const string XlanhexRequest1316 =
        "2f00000000009ee211a3ef070778006c0061006e006800650078000b00750072002000770065006c0063006f006d00650001f8";
    private const string XlanhexAnswer2325 =
        "4500000000009fe211a3ef07573d00094300720065007300630079007400680065000778006c0061006e006800650078" +
        "000b00750072002000770065006c0063006f006d00650001f8";

    // Vector: a second sender whispers "unu" to another offline recipient: the 1316 request body and the 2325
    // answer body.
    private const string KickahRequest1316 =
        "2f00000000009ee211e81b070e5000610072007400540069006d0065004b00690063006b0061006800040075006e00750001f8";
    private const string KickahAnswer2325 =
        "4300000000009fe211e81b07573d00084e0061006d0061007a0075006b006f000e5000610072007400540069006d0065" +
        "004b00690063006b0061006800040075006e00750001f8";

    public static TheoryData<string, string, string, uint> RetailNotFoundPairs => new()
    {
        { XlanhexRequest1316, XlanhexAnswer2325, "Crescythe", 0x07EFA311u },
        { KickahRequest1316, KickahAnswer2325, "Namazuko", 0x071BE811u },
    };

    /// <summary>The recipient name and the message exactly as the 1316 carried them (see WhisperHandler).</summary>
    private static (byte[] Recipient, byte[] Message, uint SenderHandle) ParseRequest(byte[] body)
    {
        Assert.Equal(body.Length - 4, BinaryPrimitives.ReadInt32LittleEndian(body));
        Assert.Equal(58014, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(6)));
        uint handle = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8));
        int targetChars = body[12];
        var recipient = body[13..(13 + targetChars * 2)];
        int p = 13 + targetChars * 2;
        int msgChars = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(p));
        var message = body[(p + 2)..(p + 2 + msgChars * 2)];
        Assert.Equal(body.Length, p + 2 + msgChars * 2);
        return (recipient, message, handle);
    }

    [Theory]
    [MemberData(nameof(RetailNotFoundPairs))]
    public void NotFoundAnswer_IsRetailsFrame_ByteForByte(string requestHex, string answerHex, string sender, uint handle)
    {
        var (recipient, message, requestHandle) = ParseRequest(Convert.FromHexString(requestHex));
        Assert.Equal(handle, requestHandle); // the answer carries the sender's own handle

        var built = WhisperHandler.BuildWhisperBlob(58015u << 16, handle, Encoding.Unicode.GetBytes(sender),
            recipient, message, WhisperHandler.TargetNotFoundResult);

        Assert.Equal(Convert.FromHexString(answerHex), built);
        Assert.Equal(0x3D57, BinaryPrimitives.ReadUInt16LittleEndian(built.AsSpan(4 + 8))); // blob+8
        Assert.Equal(0, built[4 + 10]);                                                     // blob+10 flag
    }

    [Fact]
    public void DeliveredWhisper_KeepsResultZero()
    {
        // The default is the code-0 form every delivered copy carries.
        var (recipient, message, handle) = ParseRequest(Convert.FromHexString(XlanhexRequest1316));
        var built = WhisperHandler.BuildWhisperBlob(58015u << 16, handle, Encoding.Unicode.GetBytes("Crescythe"),
            recipient, message);
        var notFound = Convert.FromHexString(XlanhexAnswer2325);
        notFound[4 + 8] = 0;
        notFound[4 + 9] = 0;
        Assert.Equal(notFound, built);
    }

    /// <summary>
    /// End to end through a real ProudServer: the 1316 to "xlanhex", who is not online, is answered by
    /// exactly the not-found 2325 to the sender, and nobody else gets anything. A second whisper, to the player who is
    /// online, proves both: the bystander's first frame is that delivery, and the sender's next frame is its echo.
    /// </summary>
    [Fact]
    public async Task OfflineRecipient_OnlyTheSenderGetsRetailsNotFoundAnswer()
    {
        const uint senderEntity = 0x07EFA311u, bystanderEntity = 0x0A5CC011u;
        await using var server = new ProudServer("Test", NullLogger.Instance);
        await server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
        using var senderClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var bystanderClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await senderClient.ConnectAsync(server.LocalEndPoint!);
        await bystanderClient.ConnectAsync(server.LocalEndPoint!);
        var deadline = DateTime.UtcNow + Generous;
        while (server.SessionCount < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Equal(2, server.SessionCount);

        ProudSession SessionOf(Socket c) =>
            server.GetSessions().Single(s => s.RemoteEndPoint!.Port == ((IPEndPoint)c.LocalEndPoint!).Port);
        var senderSession = SessionOf(senderClient);
        var bystanderSession = SessionOf(bystanderClient);
        senderSession.Crypto.SetRc4Key(Rc4Key);
        senderSession.Handshake = HandshakeState.Connected; // RMIs are dispatched only after the handshake (S-H5)

        var players = new PlayerStateManager(new NoPersistence());
        players.AddPlayer(senderSession.SessionId, new PlayerState
            { SessionId = senderSession.SessionId, Name = "Crescythe", EntityId = senderEntity });
        players.AddPlayer(bystanderSession.SessionId, new PlayerState
            { SessionId = bystanderSession.SessionId, Name = "Bystander", EntityId = bystanderEntity });
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(server);
        var handler = new WhisperHandler(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<WhisperHandler>.Instance, players, broadcast);
        server.PacketReceived += (s, id, payload) => id == handler.Opcode ? handler.HandleAsync(s, payload) : Task.CompletedTask;

        var request = Convert.FromHexString(XlanhexRequest1316);
        await senderClient.SendAsync(Encrypted(Whisper1316(1, request)), SocketFlags.None);

        var (op, body) = await ReadInternalRmiAsync(senderClient);
        Assert.Equal(2325, op);
        Assert.Equal(Convert.FromHexString(XlanhexAnswer2325), body);

        // The same message to the online player: delivered with code 0 to both, and the bystander's FIRST frame.
        var (_, message, _) = ParseRequest(request);
        var toBystander = BuildRequest(senderEntity, "Bystander", message);
        await senderClient.SendAsync(Encrypted(Whisper1316(2, toBystander)), SocketFlags.None);

        var senderRaw = Encoding.Unicode.GetBytes("Crescythe");
        var bystanderRaw = Encoding.Unicode.GetBytes("Bystander");
        var (bOp, bBody) = await ReadInternalRmiAsync(bystanderClient);
        Assert.Equal(2325, bOp);
        Assert.Equal(WhisperHandler.BuildWhisperBlob(58015u << 16, bystanderEntity, senderRaw, bystanderRaw, message), bBody);
        var (eOp, eBody) = await ReadInternalRmiAsync(senderClient);
        Assert.Equal(2325, eOp);
        Assert.Equal(WhisperHandler.BuildWhisperBlob(58015u << 16, senderEntity, senderRaw, bystanderRaw, message), eBody);
    }

    // ------------------------------------------------------------------------------------------

    /// <summary>A 64-byte test key (512-bit RC4, as the handshake installs).</summary>
    private static readonly byte[] Rc4Key = Enumerable.Range(0, 64).Select(i => (byte)(i * 37 + 11)).ToArray();

    /// <summary>A 1316 body in the client's layout: [u32 len][0][58014][handle][u8 n][name][u16 m][message].</summary>
    private static byte[] BuildRequest(uint senderHandle, string recipient, byte[] message)
    {
        var name = Encoding.Unicode.GetBytes(recipient);
        var body = new byte[4 + 8 + 1 + name.Length + 2 + message.Length];
        BinaryPrimitives.WriteInt32LittleEndian(body, body.Length - 4);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6), 58014);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), senderHandle);
        body[12] = (byte)(name.Length / 2);
        name.CopyTo(body, 13);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(13 + name.Length), (ushort)(message.Length / 2));
        message.CopyTo(body, 15 + name.Length);
        return body;
    }

    /// <summary>The plaintext of a counter-bearing RMI message: [u16 counter][0x01][u16 1316][body].</summary>
    private static byte[] Whisper1316(ushort counter, byte[] body)
    {
        var plain = new byte[5 + body.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(plain, counter);
        plain[2] = 0x01;
        BinaryPrimitives.WriteUInt16LittleEndian(plain.AsSpan(3), 1316);
        body.CopyTo(plain, 5);
        return plain;
    }

    /// <summary>[0x25][02 = RC4][CompactLen n][RC4(plaintext)], framed (as ProudCompressedFrameTests).</summary>
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
