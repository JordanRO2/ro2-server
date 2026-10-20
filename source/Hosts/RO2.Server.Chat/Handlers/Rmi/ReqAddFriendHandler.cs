using RO2.Application.Interfaces;
using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Handlers;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Network.Throttling;
using RO2.Server.Chat.Services;

namespace RO2.Server.Chat.Handlers.Rmi;

/// <summary>
/// RMI 23002 ReqAddFriend → 23508 AnsAddFriend to the requester, plus 23512 NfyAddFriend to the added
/// player if they are online.
///
/// C2S body (U32 family): [u32 innerLen = len-4][u8 nChars][name UTF-16LE], e.g. 0f000000 07 'Melodiy'.
/// The 23508 answer carries result 0 = added, 18 = no such character, 6 = already a friend or
/// 33 = the requester's list is full.
/// The checks run in this order: full list (33), no such character (18), already listed (6), added (0).
/// See <see cref="FriendStore"/> for the body layout.
/// </summary>
public class ReqAddFriendHandler : ChatRmiHandlerBase<ReqAddFriendHandler>
{
    public override ushort Opcode => ChatC2SOpcodes.ReqAddFriend; // 23002
    public override string Name => "AddFriend";
    public ReqAddFriendHandler(IServiceScopeFactory s, ILogger<ReqAddFriendHandler> l) : base(s, l) { }

    /// <summary>Size of the leading u32 <c>innerLen</c> of the C2S body (<c>innerLen == len - 4</c>).</summary>
    private const int LengthPrefixSize = 4;

    /// <summary>body+4: the u8 character count of the target name.</summary>
    private const int NameCharCountOffset = 4;

    /// <summary>body+5: the target name, UTF-16LE, <c>nChars</c> code units.</summary>
    private const int NameOffset = 5;

    /// <summary>Bytes per UTF-16 code unit of the name.</summary>
    private const int Utf16UnitSize = 2;

    /// <summary>Adds admitted per (requester, target) pair inside <see cref="PerPairWindowMs"/>.</summary>
    private const int PerPairLimit = 2;

    /// <summary>Length of the per-pair add window: 60 s.</summary>
    private const long PerPairWindowMs = 60_000;

    /// <summary>
    /// A per-(requester, target) sliding window on top of the per-character <see cref="ChatSocialThrottle"/>.
    /// A static (like <see cref="ChannelChatFloodGate"/>), so it is not a DI dependency and a reconnect does not
    /// reset it. 2 adds / 60 s per ordered pair is far above any human's rate of re-adding one specific person; it
    /// caps the add / del / re-add loop that spams a victim with 23512 NfyAddFriend.
    /// </summary>
    private static readonly SlidingWindowRateLimiter<(uint Requester, uint Target)> PerPairLimiter =
        new(PerPairLimit, PerPairWindowMs);

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    {
        // CharacterId and MessengerUid are set by ChatAuthHandler (23001); without them this connection has no
        // friend list.
        if (session.CharacterId is not uint ownerId || !MessengerUid.TryGet(session, out var ownerGid))
        {
            Logger.LogWarning("Session {Id}: 23002 before messenger login, ignored", session.SessionId);
            return;
        }

        // Bound the per-character rate of messenger social requests (each one is a Mongo name
        // lookup and/or list write). A flood is dropped before it reaches the database.
        if (!services.GetRequiredService<ChatSocialThrottle>().TryAct(ownerId))
        {
            Logger.LogWarning("Session {Id}: 23002 over the social rate limit for character {Owner}, dropped",
                session.SessionId, ownerId);
            return;
        }

        var req = payload.Span;
        if (req.Length < NameOffset
            || BinaryPrimitives.ReadUInt32LittleEndian(req) != (uint)(req.Length - LengthPrefixSize)
            || NameOffset + Utf16UnitSize * req[NameCharCountOffset] != req.Length)
        {
            Logger.LogWarning("Session {Id}: malformed 23002 body ({Len} bytes)", session.SessionId, req.Length);
            return;
        }
        var name = Encoding.Unicode.GetString(req.Slice(NameOffset, Utf16UnitSize * req[NameCharCountOffset])).TrimEnd('\0');

        var presence = services.GetRequiredService<IChannelPresenceStore>();
        var friends = services.GetRequiredService<FriendStore>();
        var broadcast = services.GetRequiredService<ChatBroadcastService>();
        var blocks = services.GetRequiredService<BlockListRepository>();

        // A full list is answered 33 BEFORE the name is looked up, so an unknown name on a full list gets 33
        // and the same name gets 18 once a delete has made room (see FriendStore.MaxFriends).
        int listed = await friends.CountAsync(ownerId, ct);
        if (FriendStore.IsListFull(listed))
        {
            Logger.LogInformation("Session {Id}: add friend \"{Name}\": list full ({Count} of {Max})",
                session.SessionId, name, listed, FriendStore.MaxFriends);
            await session.SendInternalRmiAsync(ChatS2COpcodes.AnsAddFriend,
                FriendStore.BuildAddFriendAnswer(FriendStore.AddResultListFull, null));
            return;
        }

        var target = name.Length == 0 ? null : await friends.FindCharacterByNameAsync(name);
        if (target == null)
        {
            Logger.LogInformation("Session {Id}: add friend \"{Name}\": no such character", session.SessionId, name);
            await session.SendInternalRmiAsync(ChatS2COpcodes.AnsAddFriend,
                FriendStore.BuildAddFriendAnswer(FriendStore.AddResultNotFound, null));
            return;
        }

        // A character cannot friend itself. The client refuses its own name before it ever sends 23002, so a
        // self-add reaches the server only from a forged client. There is no refusal frame for it, so it is
        // dropped with no DB write and no reply.
        if (target.Id == ownerId)
        {
            Logger.LogWarning("Session {Id}: self add-friend for character {Owner} dropped", session.SessionId, ownerId);
            return;
        }

        // Per-(requester, target) ceiling, independent of the per-character budget, so one character
        // cannot repeatedly drive the add path - and the 23512 notice - against one specific victim.
        if (!PerPairLimiter.TryAcquire((ownerId, target.Id)))
        {
            Logger.LogWarning("Session {Id}: add-friend from {Owner} to {Target} over the per-pair limit, dropped",
                session.SessionId, ownerId, target.Id);
            return;
        }

        switch (await friends.AddAsync(ownerId, target.Id))
        {
            case FriendStore.AddOutcome.AlreadyFriend:
                Logger.LogInformation("Session {Id}: add friend \"{Name}\": already a friend", session.SessionId, target.Name);
                await session.SendInternalRmiAsync(ChatS2COpcodes.AnsAddFriend,
                    FriendStore.BuildAddFriendAnswer(FriendStore.AddResultAlreadyFriend, null));
                return;
            case FriendStore.AddOutcome.ListFull:
                // A concurrent add slipped past the pre-lookup count check and filled the last slot; the
                // atomic size guard caught it. Answer 33, the same list-full result as the pre-check.
                Logger.LogInformation("Session {Id}: add friend \"{Name}\": list full (atomic guard)", session.SessionId, target.Name);
                await session.SendInternalRmiAsync(ChatS2COpcodes.AnsAddFriend,
                    FriendStore.BuildAddFriendAnswer(FriendStore.AddResultListFull, null));
                return;
        }

        Logger.LogInformation("Session {Id}: {Owner} added friend {Friend} ({FriendId})",
            session.SessionId, session.Username ?? "?", target.Name, target.Id);
        // The record's uid is the friend's GID, as in 23505 (ReqFriendListHandler.WithGids): it is above
        // 2^32, and the client keys the new friend by it.
        await session.SendInternalRmiAsync(ChatS2COpcodes.AnsAddFriend,
            FriendStore.BuildAddFriendAnswer(FriendStore.AddResultOk,
                FriendStore.Entry(target, FriendStore.IsOnline(broadcast, target.Id),
                        (await presence.GetLocationsAsync(new[] { target.Id }, ct)).GetValueOrDefault(target.Id))
                    with { Uid = CharacterGid.Of(ownerGid, target.Id) }));

        // Tell the added player who added them. The body is the requester's own name, so without a
        // resolved name (ChatAuthHandler could not load the character) there is nothing to send.
        if (string.IsNullOrEmpty(session.Username)) return;

        // No notice on an add-back: when the added player already lists the requester, no 23512 is sent.
        if (await friends.ListsFriendAsync(target.Id, ownerId, ct))
        {
            Logger.LogDebug("Session {Id}: {Friend} already lists {Owner}, no 23512",
                session.SessionId, target.Name, session.Username);
            return;
        }

        // Defensive: if the added player has blocked the requester, do not tell them they were added.
        // The add still succeeds for the requester; only the 23512 to the target is withheld.
        if (await blocks.BlocksAsync(target.Id, ownerId, ct))
        {
            Logger.LogDebug("Session {Id}: {Friend} blocks {Owner}, no 23512",
                session.SessionId, target.Name, session.Username);
            return;
        }

        var notify = FriendStore.BuildNfyAddFriend(session.Username);
        foreach (var other in broadcast.Sessions)
        {
            if (other.IsConnected && other.CharacterId == target.Id)
                await other.SendInternalRmiAsync(ChatS2COpcodes.NfyAddFriend, notify);
        }
    }
}
