using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Handlers;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Chat.Services;

namespace RO2.Server.Chat.Handlers.Rmi;

/// <summary>RMI 23006 ReqAddBlockList → 23510 AnsAddBlock.</summary>
/// <remarks>
/// <para>
/// The client sends the name the player typed (<see cref="BlockListPackets.ReadAddBlockName"/>); the
/// server resolves it to a character, stores it in the owner's block list and answers with that
/// character's uid and stored name. The name is resolved case-insensitively: a request for "iro2chatbot"
/// is answered with "iRO2ChatBot". The stored list is what 23008 sends back.
/// </para>
/// <para>
/// The record carries the character's GID, not its 32-bit id (<see cref="CharacterGid"/>): the uid is
/// above 2^32, and the client files the record in its block map under that uid and checks chat senders
/// against it by the full 64-bit value. The same uid also marks a friend as blocked: the 23510 handler
/// looks it up in the friend map (a whole-u64 search) and, on a hit, sets the friend's blocked flag and
/// refreshes the friend tab. That only matches when the friend was filed under the same GID, as 23505
/// files it (ReqFriendListHandler.WithGids). Friend uids are GIDs on every friend path (23508, 23502,
/// 23503 and C2S 23005).
/// </para>
/// <para>
/// A name that is already on the owner's list is refused with result 7 and an empty record, and the
/// list is left alone (<see cref="BlockListPackets.BuildAddBlockRefusal"/>). Answering it with a second
/// success would make the client report a new block and add the record again.
/// </para>
/// <para>
/// A name that resolves to no character is refused with result 18 and the same empty record
/// (<see cref="BlockListPackets.AddBlockResultNotFound"/>), and the client shows "The character name
/// you've entered does not exist." for it. Sending nothing would leave the block request unanswered.
/// </para>
/// </remarks>
public class ReqAddBlockHandler : ChatRmiHandlerBase<ReqAddBlockHandler>
{
    public override ushort Opcode => ChatC2SOpcodes.ReqAddBlockList; // 23006
    public override string Name => "AddBlock";
    public ReqAddBlockHandler(IServiceScopeFactory s, ILogger<ReqAddBlockHandler> l) : base(s, l) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogDebug("Session {Id}: {Name}", session.SessionId, Name);

        // The owner is the character that logged into the messenger with 23001 (ChatAuthHandler), which
        // sets both its id and its full GID; the GID's high dword is what builds the target's GID.
        if (session.CharacterId is not uint ownerId || !MessengerUid.TryGet(session, out var ownerGid))
        {
            Logger.LogWarning("Session {Id}: {Name} before messenger login - ignored", session.SessionId, Name);
            return;
        }

        // Per-character cap over the messenger social requests (see ChatSocialThrottle).
        if (!services.GetRequiredService<ChatSocialThrottle>().TryAct(ownerId))
        {
            Logger.LogWarning("Session {Id}: {Name} over the social rate limit for character {Owner}, dropped",
                session.SessionId, Name, ownerId);
            return;
        }

        var requested = BlockListPackets.ReadAddBlockName(payload.Span);
        if (string.IsNullOrEmpty(requested))
        {
            Logger.LogWarning("Session {Id}: {Name} body unreadable ({Size} bytes)", session.SessionId, Name, payload.Length);
            return;
        }

        var repo = services.GetRequiredService<BlockListRepository>();
        var target = await repo.FindCharacterByNameAsync(requested, ct);
        if (target is null)
        {
            Logger.LogInformation("Session {Id}: block request for unknown character '{Target}' - refused with {Result}",
                session.SessionId, requested, BlockListPackets.AddBlockResultNotFound);
            await session.SendInternalRmiAsync(ChatS2COpcodes.AnsAddBlock,
                BlockListPackets.BuildAddBlockRefusal(BlockListPackets.AddBlockResultNotFound));
            return;
        }

        // A character cannot block itself. The client prevents it, so a self-block reaches the server
        // only from a forged client. There is no refusal frame for it, so it is dropped with no DB write and no
        // reply.
        if (target.Id == ownerId)
        {
            Logger.LogWarning("Session {Id}: self block for character {Owner} dropped", session.SessionId, ownerId);
            return;
        }

        switch (await repo.AddAsync(ownerId, target.Id, target.Name, ct))
        {
            case BlockAddOutcome.AlreadyRegistered:
                Logger.LogInformation("Session {Id}: character {Owner} already blocks {Target} ({TargetId}) - refused with {Result}",
                    session.SessionId, ownerId, target.Name, target.Id, BlockListPackets.AddBlockResultAlreadyRegistered);
                await session.SendInternalRmiAsync(ChatS2COpcodes.AnsAddBlock,
                    BlockListPackets.BuildAddBlockRefusal(BlockListPackets.AddBlockResultAlreadyRegistered));
                return;
            case BlockAddOutcome.ListFull:
                // There is no block-list-full frame and no client result code for it; the defensive cap
                // (BlockListRepository.MaxBlocked) is reached only by a forged client, so the request is dropped with
                // no reply.
                Logger.LogWarning("Session {Id}: character {Owner} block list full ({Max}) - request for {Target} dropped",
                    session.SessionId, ownerId, BlockListRepository.MaxBlocked, target.Name);
                return;
        }

        var targetGid = CharacterGid.Of(ownerGid, target.Id);
        Logger.LogInformation("Session {Id}: character {Owner} blocked {Target} ({TargetId}, gid 0x{Gid:X16})",
            session.SessionId, ownerId, target.Name, target.Id, targetGid);

        await session.SendInternalRmiAsync(ChatS2COpcodes.AnsAddBlock,
            BlockListPackets.BuildAddBlockBody(targetGid, target.Name));
    }
}
