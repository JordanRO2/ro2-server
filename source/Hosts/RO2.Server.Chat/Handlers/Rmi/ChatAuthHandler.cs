using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Handlers;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

namespace RO2.Server.Chat.Handlers.Rmi;

/// <summary>
/// Handler for Chat RMI 23001 - ReqLoginMessenger (ChatAuth).
///
/// The client connects to the Chat server (port 7401) after the Zone hands it the login token in 2733
/// (AnsPlayerLoginKey); it replays that token verbatim as this body:
///   [data_len:u32=56][char_uid:u64][token:16][ip_len:u16 (wchar count)][ip: UTF-16LE][port:u16].
/// The char_uid is the player's character GID, the same value as its 2310 GID (see <see cref="MessengerUid"/>),
/// and the client uses it as its messenger identity: it is the 23018 senderUid and the private-room
/// member uid. It is not listed in the player's own 23505, which is the friend list.
///
/// Reply sequence: 23501 ack (u16 result code, 0 = success), then RMI 23534 once per community
/// channel to auto-enroll the player, body [channelIndex:u16][channelGuid:16] (Public=12/Trade=13/Party=14),
/// recorded in <see cref="CommunityChannelMembership"/>.
/// After that, 23502 goes to the online players who have this character as a friend (FriendPresence).
/// </summary>
public class ChatAuthHandler : ChatRmiHandlerBase<ChatAuthHandler>
{
    public override ushort Opcode => ChatC2SOpcodes.ReqLoginMessenger;
    public override string Name => "ChatAuth";

    // 23001 body layout: [u32 dataLen][u64 charUid][16B token][...].
    private const int DataLenSize = sizeof(uint);
    private const int CharUidSize = sizeof(ulong);
    private const int TokenSize = 16;

    // 23501 AnsLoginMessenger body: a bare u16 result code, no envelope. 0 = success; retail refuses with 1 or 2.
    private const ushort LoginResultSuccess = 0;
    private const ushort LoginResultRefused = 1;

    // 23534 NfyPublicChatRoomAutoJoin body: [u16 channelIndex][16B channelGuid].
    private const int ChannelIndexSize = sizeof(ushort);
    private const int ChannelGuidSize = 16;

    public ChatAuthHandler(IServiceScopeFactory scopeFactory, ILogger<ChatAuthHandler> logger)
        : base(scopeFactory, logger)
    {
    }

    protected override async Task HandleAsync(
        ProudSession session,
        ReadOnlyMemory<byte> payload,
        IServiceProvider services,
        CancellationToken ct)
    {
        Logger.LogInformation("Session {Id}: ChatAuth (RMI 23001) received ({Size} bytes)",
            session.SessionId, payload.Length);

        // The token is the one-shot key the Zone bound to {accountId, charUid}; it is redeemed here exactly once.
        // The identity comes from that server-issued record, never from the charUid in the body: anyone can open
        // a raw connection to the Chat port and send another player's charUid, but without the matching key there
        // is nothing to redeem, so the login is refused.
        TryReadMessengerToken(payload.Span, out var packetCharUid, out _);
        var tokens = services.GetRequiredService<IOneShotTokenStore>();
        var token = await AuthenticateAsync(
            payload, hex => tokens.ConsumeAsync(hex, OneShotTokens.PurposeMessenger, ct: ct));
        if (token is null)
        {
            Logger.LogWarning("Session {Id}: ChatAuth REJECTED - missing/invalid/expired/already-used messenger " +
                "key (packet claimed CharUID=0x{Uid:X16})", session.SessionId, packetCharUid);
            await RejectAsync(session);
            return;
        }

        var charUid = token.CharacterUid;
        Logger.LogInformation("Session {Id}: ChatAuth - CharUID=0x{Uid:X16} (from token, account {Account})",
            session.SessionId, charUid, token.AccountId);

        // Two views of the same uid. CharacterId keeps the low dword, the database id every Chat store is keyed
        // by; MessengerUid keeps all 64 bits, because the client names itself by the whole GID (it is the 23018
        // senderUid). AccountId comes from the token, so the session carries the authenticated account.
        session.CharacterId = (uint)charUid;
        session.AccountId = token.AccountId;
        MessengerUid.Set(session, charUid);

        // Resolve and cache the character name so room chat can show the sender.
        try
        {
            var characters = services.GetRequiredService<ICharacterRepository>();
            var character = await characters.GetByIdAsync((uint)charUid);
            if (character != null)
                session.Username = character.Name;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Session {Id}: could not resolve character name", session.SessionId);
        }

        // Reaching here means the key was valid, so answer success.
        await session.SendInternalRmiAsync(ChatS2COpcodes.AnsLoginMessenger, LoginResultBody(LoginResultSuccess));

        // A successful 23501 is followed by one 23534 per community channel, in index order (Public 12, Trade 13,
        // Party 14), and none after a failure. The GUIDs must match what 23513/23018/23528 use, which
        // CommunityChannels guarantees. There is no level gate. The membership is recorded before the sends, so a
        // chat line that arrives right after the first 23534 already counts this session; a 23013 leave takes a
        // channel out, 23012 puts it back, and 23528 reaches members only (CommunityChannelMembership).
        CommunityChannelMembership.JoinAll(session);
        foreach (var ch in CommunityChannels.All)
        {
            var body = new byte[ChannelIndexSize + ChannelGuidSize];
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0), ch.Index);
            ch.Guid.CopyTo(body, ChannelIndexSize);
            await session.SendInternalRmiAsync(ChatS2COpcodes.NfyPublicChatRoomAutoJoin, body);
        }

        Logger.LogInformation("Session {Id}: auto-joined community channels Public/Trade/Party",
            session.SessionId);

        // 23502 NfyLoginPlayer_RegisterFriend goes to every online player who has this character in their friend
        // list, right after this login's 23501/23534 answers (see FriendPresence). The login is already answered
        // by this point, so a lookup failure is logged and does not abort it.
        try
        {
            await services.GetRequiredService<FriendPresence>().NotifyLoginAsync(
                services.GetRequiredService<ChatBroadcastService>(),
                session);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Session {Id}: could not send the friend login notice (23502)", session.SessionId);
        }
    }

 /// <summary>
 /// Refuse a messenger login: a 23501 with a non-zero result code (retail uses 1 and 2), the in-protocol
 /// failure answer. No identity is set and no channel joined, so a refused session cannot act as any
 /// character - which is the whole point of rejecting a forged or absent key.
 /// </summary>
    private Task RejectAsync(ProudSession session)
        => session.SendInternalRmiAsync(ChatS2COpcodes.AnsLoginMessenger, LoginResultBody(LoginResultRefused));

 /// <summary>The 23501 body: the u16 result code, little-endian.</summary>
    private static byte[] LoginResultBody(ushort result)
    {
        var body = new byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(body, result);
        return body;
    }

 /// <summary>
 /// Read the one-shot messenger key out of a 23001 body: [u32 dataLen][u64 charUid][16B token][...].
 /// <paramref name="packetCharUid"/> is the charUid the body claims (for logging only - it is not trusted).
 /// Returns false when the body is too short to carry the 16-byte token.
 /// </summary>
    internal static bool TryReadMessengerToken(ReadOnlySpan<byte> body, out ulong packetCharUid, out string tokenHex)
    {
        packetCharUid = 0;
        tokenHex = "";
        if (body.Length < DataLenSize + CharUidSize + TokenSize)
            return false;

        var reader = new PacketReader(body);
        reader.ReadUInt32();                 // dataLen (unused)
        packetCharUid = reader.ReadUInt64(); // claimed identity, deliberately ignored for auth
        tokenHex = Convert.ToHexString(reader.ReadBytes(TokenSize));
        return true;
    }

 /// <summary>
 /// The authentication decision, lifted out so it can be tested without a socket or a live Mongo: read the
 /// key from the body and redeem it through <paramref name="consume"/>. Returns the bound identity, or null
 /// to reject (no key in the body, or the key was missing/invalid/expired/already-used). The packet's own
 /// charUid is never passed to the caller, so the identity can only come from the redeemed token.
 /// </summary>
    internal static async Task<OneShotToken?> AuthenticateAsync(
        ReadOnlyMemory<byte> body, Func<string, Task<OneShotToken?>> consume)
    {
        if (!TryReadMessengerToken(body.Span, out _, out var tokenHex))
            return null;
        return await consume(tokenHex);
    }
}
