namespace RO2.Network.ProudNet;

/// <summary>
/// ProudNet protocol constants.
/// </summary>
public static class ProudConstants
{
    /// <summary>
    /// Magic number that opens every ProudNet frame (0x5713, written little-endian: the bytes [13 57]).
    /// Frame: [u16 magic][scalar length][message].
    /// </summary>
    public const ushort NetMagic = 0x5713;

    /// <summary>
    /// ProudNet protocol version (196977, 0x00030171). The server reports it to the client in the
    /// connect-success message: <c>[u32 hostId][u32 NetVersion][u8 1]</c>, at offset +4.
    /// </summary>
    public const uint NetVersion = 196977;
}

/// <summary>
/// ProudNet message types: the first byte of every message in a [13 57] frame, and of every message
/// that the 0x25 / 0x26 / 0x27 wrappers carry.
/// </summary>
/// <remarks>
/// <para>
/// C2S means the server dispatcher handles the type and the client writes it. S2C means the client
/// dispatcher handles it and the server writes it. P2P means a client addresses it to another
/// client, so this server never receives it as its own type. Where the server dispatcher calls a named
/// handler the member takes that name; what each comment says the code does is the reference for the
/// member, not the name.
/// </para>
/// <para>
/// Every game frame is a 0x01 RMI. Core messages such as 0x1B or 0x1D are transport-level and are not
/// part of the RMI stream.
/// </para>
/// <para>
/// Also defined by the protocol but not listed here: 0x2E, written when a UDP socket is assigned to a
/// client, for which neither dispatcher has a case; and 0x30, which both dispatchers accept and ignore.
/// 0x41 and 0x42, which this enum used to call EncryptedRmi and CompressedRmi, are not valid message
/// types and have been removed.
/// </para>
/// </remarks>
public enum ProudCoreOpCode : byte
{
    // ---- Payload -------------------------------------------------------------------------------

    /// <summary>Both directions: an RMI, [u16 rmiId][body]. Every game RMI and every ProudNet-internal RMI
    /// (1001, 1108, ...) travels as this type. The old name of this value was InternalRmi, and <c>Rmi</c>
    /// used to mean 0x25 (see <see cref="EncryptedReliable"/>).</summary>
    Rmi = 0x01,

    /// <summary>Both directions: a user message.</summary>
    UserMessage = 0x02,

    // ---- Client-server handshake (TCP) ---------------------------------------------------------

    /// <summary>S2C: sent when a connecting client is purged for taking too long to complete the handshake.
    /// The client raises error 7 and leaves the connecting state.</summary>
    ConnectServerTimedout = 0x03,

    /// <summary>S2C: the server's answer to <see cref="RequestServerConnectionHint"/>. It carries the RSA
    /// public key, and ConnectionInitHandlerBase sends it (as bytes, under the old name PeerAccept).</summary>
    NotifyServerConnectionHint = 0x04,

    /// <summary>C2S: the client's RSA-encrypted session key. ProudNet calls it NotifyCSEncryptedSessionKey.
    /// That identifier still names 0x10 here (see the old names below), so this value keeps the name
    /// EncryptedSessionKeyHandlerBase binds to.</summary>
    EncryptedSessionKey = 0x05,

    /// <summary>S2C: the answer to <see cref="EncryptedSessionKey"/> when the key decrypts (when it does not,
    /// the server sends <see cref="NotifyServerDeniedConnection"/>). ProudNet calls it
    /// NotifyCSSessionKeySuccess, an identifier that still names 0x31 here. EncryptedSessionKeyHandlerBase
    /// sends it as the byte 0x06.</summary>
    SessionKeySuccess = 0x06,

    /// <summary>C2S: the client's connection request data. ProudNet calls it
    /// NotifyServerConnectionRequestData, an identifier that still names 0x14 here, so this value keeps the
    /// name the three ClientConnectInfoHandlers bind to.</summary>
    ClientConnectInfo = 0x07,

    /// <summary>S2C: rejects a connection on a protocol version mismatch; sent only from the two handshake
    /// handlers' failure paths. The client raises error 8 and leaves the connecting state.</summary>
    NotifyProtocolVersionMismatch = 0x08,

    /// <summary>S2C: denies a connection, sent from the handshake handlers' failure branches. The client
    /// raises error 9 and leaves the connecting state.</summary>
    NotifyServerDeniedConnection = 0x09,

    /// <summary>S2C: the answer to <see cref="ClientConnectInfo"/> that assigns the client its HostID.
    /// ProudNet calls it NotifyServerConnectSuccess, an identifier that still names 0x32 here.
    /// The ClientConnectInfoHandlers send it as bytes.</summary>
    ConnectSuccess = 0x0A,

    /// <summary>C2S: the first message on a connection, answered with
    /// <see cref="NotifyServerConnectionHint"/>.</summary>
    RequestServerConnectionHint = 0x2F,

    // ---- Client-server UDP hole punching ------------------------------------------------------
    // This server opens no UDP socket and never sends RequestStartServerHolepunch, so none of these
    // reaches it today.

    /// <summary>S2C: carries a 16-byte UDP magic number that starts the client-server hole punch.</summary>
    RequestStartServerHolepunch = 0x0B,

    /// <summary>C2S: the client's hole-punch probe, answered with <see cref="ServerHolepunchAck"/>.</summary>
    ServerHolepunch = 0x0C,

    /// <summary>S2C: the answer to <see cref="ServerHolepunch"/>; the client replies with
    /// <see cref="NotifyHolepunchSuccess"/>.</summary>
    ServerHolepunchAck = 0x0D,

    /// <summary>C2S: the client reports the hole punch worked; answered with
    /// <see cref="NotifyClientServerUdpMatched"/>.</summary>
    NotifyHolepunchSuccess = 0x0E,

    /// <summary>S2C: confirms the UDP path, carrying a 16-byte magic number.</summary>
    NotifyClientServerUdpMatched = 0x0F,

    /// <summary>C2S: a peer-to-peer hole-punch probe addressed to the server, answered with
    /// <see cref="PeerUdp_ServerHolepunchAck"/>.</summary>
    PeerUdp_ServerHolepunch = 0x10,

    /// <summary>S2C: the answer to <see cref="PeerUdp_ServerHolepunch"/>.</summary>
    PeerUdp_ServerHolepunchAck = 0x11,

    /// <summary>C2S: the client reports a peer-to-peer hole punch succeeded.</summary>
    PeerUdp_NotifyHolepunchSuccess = 0x12,

    // ---- Relay through the server (P2P groups) -------------------------------------------------

    /// <summary>C2S: a reliable message to peers, relayed by the server, which forwards it as
    /// <see cref="ReliableRelay2"/>.</summary>
    ReliableRelay1 = 0x14,

    /// <summary>C2S: the unreliable counterpart of <see cref="ReliableRelay1"/>.</summary>
    UnreliableRelay1 = 0x15,

    /// <summary>C2S: <see cref="UnreliableRelay1"/> with a compressed destination list.</summary>
    UnreliableRelay1_RelayDestListCompressed = 0x16,

    /// <summary>C2S: a linger-data frame for a peer, which the server forwards as
    /// <see cref="LingerDataFrame2"/>.</summary>
    LingerDataFrame1 = 0x17,

    /// <summary>S2C: the relayed form of <see cref="ReliableRelay1"/>.</summary>
    ReliableRelay2 = 0x18,

    /// <summary>S2C: the relayed form of <see cref="UnreliableRelay1"/>.</summary>
    UnreliableRelay2 = 0x19,

    /// <summary>S2C: the relayed form of <see cref="LingerDataFrame1"/>.</summary>
    LingerDataFrame2 = 0x1A,

    /// <summary>S2C: a server-routed multicast. The client reads a host-id list and a payload,
    /// forwards the payload as <see cref="S2CRoutedMulticast2"/> to each listed peer over UDP, and then
    /// handles it itself.</summary>
    S2CRoutedMulticast1 = 0x23,

    /// <summary>P2P: the peer leg of <see cref="S2CRoutedMulticast1"/>, sent from client to client. The
    /// server dispatcher has no case for it, so a client never sends it to a server.
    /// ReliablePingHandlerBase is bound here under the old name ReliablePing, so it never runs.</summary>
    S2CRoutedMulticast2 = 0x24,

    // ---- Liveness and timing -------------------------------------------------------------------

    /// <summary>
    /// C2S: the client's ping, <c>[0x1B][u64 clientTime][u32 smoothed ping]</c>, where the u32 is the
    /// client's own running ping estimate. The client sends it once it holds a HostID, over UDP when a UDP
    /// path exists and over TCP otherwise. This server has no UDP path, so the ping always comes over TCP.
    /// The server reads the u64 and u32 and answers <see cref="UnreliablePong"/>.
    /// UnreliablePingHandlerBase answers it the same way.
    /// </summary>
    UnreliablePing = 0x1B,

    /// <summary>C2S: body-less. The server only updates its speed-hack detector and sends no answer. A bare
    /// 0x1D sent back for it would be dropped by the client's pong reader at its first 8-byte read.</summary>
    SpeedHackDetectorPing = 0x1C,

    /// <summary>
    /// S2C: the answer to <see cref="UnreliablePing"/>,
    /// <c>[0x1D][u64 echoed clientTime][u64 serverTime]</c>. The client reads both as 8-byte values and
    /// derives its ping and server-time offset from them; if the second read comes up short it does
    /// nothing. Built by UnreliablePongMessage.
    /// </summary>
    UnreliablePong = 0x1D,

    /// <summary>S2C: body-less keep-alive written on a timer, gated on the client's last-receive time
    /// against 1.5x the ping interval. The client accepts it and does nothing with it.</summary>
    ServerKeepAliveTouch = 0x1E,

    /// <summary>P2P: peer hole punch.</summary>
    PeerUdp_PeerHolepunch = 0x1F,

    /// <summary>P2P: the answer to <see cref="PeerUdp_PeerHolepunch"/>.</summary>
    PeerUdp_PeerHolepunchAck = 0x20,

    /// <summary>P2P: a peer ping over the peer's UDP path, [u64 time]. The receiving peer
    /// echoes the time back as <see cref="P2PUnreliablePong"/>.</summary>
    P2PUnreliablePing = 0x21,

    /// <summary>P2P: the echo of <see cref="P2PUnreliablePing"/>, from which the peer's ping is derived.</summary>
    P2PUnreliablePong = 0x22,

    /// <summary>P2P: a peer ping, [u64 time], sent to each P2P peer's host id through the client's general
    /// send. The receiving peer echoes the time back as <see cref="P2PReliablePong"/>.</summary>
    P2PReliablePing = 0x31,

    /// <summary>P2P: the echo of <see cref="P2PReliablePing"/>, from which the peer's ping is derived.</summary>
    P2PReliablePong = 0x32,

    /// <summary>C2S (and P2P): a receive-speed probe; the server answers
    /// <see cref="ReplyReceiveSpeedAtReceiverSide_NoRelay"/>.</summary>
    RequestReceiveSpeedAtReceiverSide_NoRelay = 0x28,

    /// <summary>S2C (and P2P): the answer to <see cref="RequestReceiveSpeedAtReceiverSide_NoRelay"/>.</summary>
    ReplyReceiveSpeedAtReceiverSide_NoRelay = 0x29,

    // ---- Wrappers: the message they carry is re-dispatched by its own first byte ---------------

    /// <summary>
    /// Both directions: an encrypted RELIABLE message,
    /// <c>[0x25][u8 encMode][CompactLen n][n bytes cipher]</c>, which decrypts to
    /// <c>[u16 encryptCount][message]</c>. A reliable send is tagged 37 and carries the counter
    /// (NextEncryptCount); the receiver checks the counter only for 37. This server also writes an
    /// encMode-0 form, [rmiId][body] in the clear (ProudSession.SendGameRmiAsync). The old name of this
    /// value was Rmi.
    /// </summary>
    EncryptedReliable = 0x25,

    /// <summary>
    /// Both directions: an encrypted UNRELIABLE message. It has the same header as
    /// <see cref="EncryptedReliable"/> but no counter, so it decrypts straight to the message. Every
    /// non-reliable send is tagged 38, and compressed whispers arrive inside it. This is NOT a ping. The
    /// old name of this value was UnreliablePing, and that name made the ping handlers bind here, where
    /// ProudSession never raises a core message.
    /// </summary>
    EncryptedUnreliable = 0x26,

    /// <summary>Both directions: <c>[0x27][CompactLen deflatedLen][CompactLen inflatedLen][zlib]</c>;
    /// both dispatchers inflate it and re-dispatch the contents.</summary>
    Compressed = 0x27,

    // ---- Old names, still referenced by files outside ProudNet/ -------------------------------
    // Each alias keeps the VALUE those files bind to or send today, so no handler moves and no byte on
    // the wire changes. The ones marked "misnomer" name a value the protocol uses for something else.
    // When the references are renamed, delete this block.

    /// <summary>Old name of <see cref="Rmi"/> (ProudCompressedFrameTests).</summary>
    InternalRmi = Rmi,

    /// <summary>Old name of <see cref="RequestServerConnectionHint"/> (ConnectionInitHandlerBase).</summary>
    ConnectionInit = RequestServerConnectionHint,

    /// <summary>Old name of <see cref="UnreliablePing"/> (HeartbeatHandlerBase).</summary>
    Heartbeat_1B = UnreliablePing,

    /// <summary>Old name of <see cref="SpeedHackDetectorPing"/> (HeartbeatHandlerBase).</summary>
    Heartbeat_1C = SpeedHackDetectorPing,

    /// <summary>Misnomer. 0x10 is <see cref="PeerUdp_ServerHolepunch"/>, a client-to-server UDP message, and
    /// ProudNet's NotifyCSEncryptedSessionKey is <see cref="EncryptedSessionKey"/> (0x05).
    /// LegacySessionKeyHandlerBase binds to this value.</summary>
    NotifyCSEncryptedSessionKey = PeerUdp_ServerHolepunch,

    /// <summary>Misnomer. 0x31 is <see cref="P2PReliablePing"/>, and ProudNet's NotifyCSSessionKeySuccess is
    /// <see cref="SessionKeySuccess"/> (0x06). LegacySessionKeyHandlerBase sends this value.</summary>
    NotifyCSSessionKeySuccess = P2PReliablePing,

    /// <summary>Misnomer. 0x14 is <see cref="ReliableRelay1"/>, and ProudNet's
    /// NotifyServerConnectionRequestData is <see cref="ClientConnectInfo"/> (0x07).
    /// ServerConnectionRequestDataHandlerBase binds to this value.</summary>
    NotifyServerConnectionRequestData = ReliableRelay1,

    /// <summary>Misnomer. 0x32 is <see cref="P2PReliablePong"/> (the client sends it only to echo a 0x31),
    /// and ProudNet's NotifyServerConnectSuccess is <see cref="ConnectSuccess"/> (0x0A).
    /// ServerConnectionRequestDataHandlerBase sends this value and the ServerConnectSuccessHandlers bind to it.</summary>
    NotifyServerConnectSuccess = P2PReliablePong,
}

/// <summary>
/// Extended ProudNet opcodes (16-bit, 64001-64519)
/// Used for P2P and UDP management
/// </summary>
public enum ProudOpCode : ushort
{
    // P2P Group management
    P2PGroup_MemberJoin = 64001,
    P2PGroup_MemberLeave = 64002,
    // UDP
    RequestCreateUdpSocket = 64100,
    CreateUdpSocketAck = 64101,
    // ... more to be discovered
}

/// <summary>
/// Encrypt-mode byte of a 0x25 / 0x26 wrapper (<c>[type][encMode][CompactLen n][n bytes cipher]</c>).
/// </summary>
public enum EncryptMode
{
    /// <summary>Not encrypted. Never sent by the client; this server's own plaintext 0x25 form uses it.</summary>
    None = 0,

    /// <summary>AES: the session key negotiated in the handshake; the block carries a CRC32 and a pad length.</summary>
    Secure = 1,

    /// <summary>RC4 fast mode: a stream cipher keyed by the 64-byte fast key received in the handshake.</summary>
    Fast = 2
}
