using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

namespace RO2.Server.Login.Handlers.Core;

/// <summary>
/// Handles the ProudNet ClientConnectInfo (0x07) message, which the client sends after the session key ack.
/// The reply is ConnectSuccess (0x0A) batched with RMI 1108 (JoinServerComplete).
/// </summary>
public class ClientConnectInfoHandler : LoginCoreHandlerBase<ClientConnectInfoHandler>
{
    public override ProudCoreOpCode Opcode => ProudCoreOpCode.ClientConnectInfo;
    public override string Name => "ClientConnectInfo";

    /// <summary>Byte size of the ConnectSuccess timestamp field (u32, little-endian).</summary>
    private const int TimestampSize = 4;

    /// <summary>Byte size of the session GUID field.</summary>
    private const int SessionGuidSize = 16;

    /// <summary>Flag byte 0 of the ConnectSuccess flags field: on (1).</summary>
    private const byte ConnectFlag0 = 0x01;

    /// <summary>Flag byte 1 of the ConnectSuccess flags field: off (0).</summary>
    private const byte ConnectFlag1 = 0x00;

    /// <summary>Flag byte 2 of the ConnectSuccess flags field: on (1).</summary>
    private const byte ConnectFlag2 = 0x01;

    /// <summary>Flag byte 3 of the ConnectSuccess flags field: on (1).</summary>
    private const byte ConnectFlag3 = 0x01;

    /// <summary>
    /// Fixed four-byte flags field of the ConnectSuccess message, sent unchanged on every connection: the bytes
    /// <see cref="ConnectFlag0"/> to <see cref="ConnectFlag3"/> in order (01 00 01 01). Their individual meaning is not
    /// established. The field sits between the session GUID and the client address.
    /// </summary>
    private static ReadOnlySpan<byte> ConnectFlags => [ConnectFlag0, ConnectFlag1, ConnectFlag2, ConnectFlag3];

    /// <summary>Byte size of the address-length prefix that precedes the client IP string.</summary>
    private const int AddressLengthSize = 1;

    /// <summary>Byte size of the client port field (u16, little-endian).</summary>
    private const int PortSize = 2;

    public ClientConnectInfoHandler(ILogger<ClientConnectInfoHandler> logger) : base(logger)
    {
    }

    protected override async Task HandleCoreAsync(
        ProudSession session,
        ReadOnlyMemory<byte> payload,
        CancellationToken ct)
    {
        Logger.LogDebug("Session {SessionId}: Received 0x07 (ClientConnectInfo) - {Size} bytes",
            session.SessionId, payload.Length);

        
        
        
        // ConnectSuccess layout: [opcode 0x0A][u32 timestamp][16 session GUID][4 flags]
        //                        [u8 address length][client IP as ASCII][u16 client port]
        // The address and port are the client's own as the server sees them, not the server's.
        var clientIp = session.RemoteEndPoint?.Address.ToString() ?? "127.0.0.1";
        var clientPort = (ushort)(session.RemoteEndPoint?.Port ?? 0);
        var responseSize = sizeof(byte) + TimestampSize + SessionGuidSize + ConnectFlags.Length
            + AddressLengthSize + clientIp.Length + PortSize;
        var connectResponse = new byte[responseSize];
        int respOffset = 0;

        connectResponse[respOffset++] = (byte)ProudCoreOpCode.ConnectSuccess;

        var timestamp = (uint)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() & 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(connectResponse.AsSpan(respOffset), timestamp);
        respOffset += TimestampSize;

        var sessionGuid = Guid.NewGuid().ToByteArray();
        sessionGuid.CopyTo(connectResponse.AsSpan(respOffset));
        respOffset += SessionGuidSize;

        ConnectFlags.CopyTo(connectResponse.AsSpan(respOffset));
        respOffset += ConnectFlags.Length;

        connectResponse[respOffset++] = (byte)clientIp.Length;

        System.Text.Encoding.ASCII.GetBytes(clientIp).CopyTo(connectResponse.AsSpan(respOffset));
        respOffset += clientIp.Length;

        BinaryPrimitives.WriteUInt16LittleEndian(connectResponse.AsSpan(respOffset), clientPort);
        respOffset += PortSize;

        Logger.LogDebug("Session {SessionId}: Sending 0x0A + RMI 1108 (batched)",
            session.SessionId);

        var frame0A = ProudFrameCodec.EncodeFrame(connectResponse.AsSpan(0, respOffset));

        // Internal RMI body: [message type 0x01 (Rmi)][u16 RMI id 1108, little-endian], no payload
        var internal1108 = new byte[1 + sizeof(ushort)];
        internal1108[0] = (byte)ProudCoreOpCode.Rmi;
        BinaryPrimitives.WriteUInt16LittleEndian(internal1108.AsSpan(1), ProudNetS2COpcodes.JoinServerComplete);
        var frame1108 = ProudFrameCodec.EncodeFrame(internal1108);

        // Both frames go out in a single TCP write so the client processes them together. Sent separately,
        // the client can handle 0x0A alone and send RMI 1019 before 1108 arrives, which leaves its stub
        // pointer uninitialized.
        var combined = new byte[frame0A.Length + frame1108.Length];
        frame0A.CopyTo(combined, 0);
        frame1108.CopyTo(combined, frame0A.Length);
        await session.SendRawBytesAsync(combined, ct);

        session.Handshake = HandshakeState.Connected;
    }
}
