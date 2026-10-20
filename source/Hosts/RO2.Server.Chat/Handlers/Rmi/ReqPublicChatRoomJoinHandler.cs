using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

namespace RO2.Server.Chat.Handlers.Rmi;

/// <summary>
/// RMI 23012 ReqPublicChatRoomJoin, answered with 23518 AnsPublicChatRoomJoin: a player joins a community
/// channel, normally one it left with 23013.
/// </summary>
/// <remarks>
/// <para>
/// How the client sends it. The chat join-channel slash command (its prompts are string_ui 9517 "Please enter the
/// name of channel." and 9767 "Channel name can be up to a maximum of 10 characters.") looks the typed name up in
/// the messenger manager. When the name is a known public channel whose position index is valid, it pushes the
/// channel's u16 key and its 16-byte GUID to the 23012 proxy (a u16 then a Blob16). The key matches the index of
/// our 23534, so it is 12/13/14, and the 16 bytes are the GUID our 23513/23534 gave the channel. Only the slot
/// is bounds-checked, so the command is sent for a channel the player is still in, too.
/// </para>
/// <para>
/// Retail has no 23012 or 23518 traffic observed. A player is in every channel from login (23534 x3), so the
/// command does something only after a leave: 23013 on the channel's GUID, answered 23519, whose consumer
/// clears the channel's joined byte but keeps its tab. Retail has such a leave, just no re-join after one.
/// </para>
/// <para>
/// The answer, 23518, is read by the client as a word then a 16-byte GUID, NONE envelope, 18 bytes:
/// <c>[u16 result][16B guid]</c>. Its consumer, with
/// result 0, finds the channel by the GUID, sets the joined byte to 1, prints string_ui 9519
/// "Enter Channel : [@1. @2]" and refreshes the channel tab, the same thing the 23534 consumer
/// does; with any other result it shows a result-code message, whose arm 10 shows 9726
/// "Cannot find channel room.". That layout comes from the client's reader alone: there is no retail frame.
/// It matches its sibling 23519, whose frames are <c>[u16 0][guid]</c>
/// (<see cref="PrivateChatRooms.BuildExitAnswer"/>), and the <c>[u16][guid]</c> of 23534.
/// </para>
/// <para>
/// What the server decides, from the membership it holds (<see cref="CommunityChannelMembership"/>: joined at
/// login, left with 23013): a GUID that is no channel of <see cref="CommunityChannels.All"/> is answered 10; a
/// channel the session is still in is answered 23 (string_ui 9516 "Already participating in
/// the target channel."), and nothing changes; any other channel is joined and answered 0, after which the
/// session receives the channel's 23528 lines again. The codes come from the client's result switch alone.
/// </para>
/// </remarks>
public class ReqPublicChatRoomJoinHandler : ChatRmiHandlerBase<ReqPublicChatRoomJoinHandler>
{
 /// <summary>23518 result: joined. The client's consumer takes the success branch only on 0.</summary>
    public const ushort ResultJoined = 0;

 /// <summary>23518 result for a GUID no channel has: arm 10 of the client's messenger result-code switch,
 /// string_ui 9726 "Cannot find channel room.".</summary>
    public const ushort ResultChannelNotFound = 10;

 /// <summary>23518 result for a channel the session is already in: arm 23 of the client's messenger result-code switch,
 /// string_ui 9516 "Already participating in the target channel.". From the client's reader alone.</summary>
    public const ushort ResultAlreadyParticipating = 23;

 /// <summary>Request and answer are both 18 bytes: the fixed <c>[u16][Blob16]</c>.</summary>
    public const int BodyLength = 18;

    /// <summary>Size of the u16 at the start of the body: the request's channel index, the answer's result.</summary>
    private const int HeadLength = sizeof(ushort);

    /// <summary>Size of the channel GUID that follows the u16 in both bodies.</summary>
    private const int GuidLength = 16;

    public override ushort Opcode => ChatC2SOpcodes.ReqPublicChatRoomJoin; // 23012
    public override string Name => "PublicRoomJoin";
    public ReqPublicChatRoomJoinHandler(IServiceScopeFactory s, ILogger<ReqPublicChatRoomJoinHandler> l) : base(s, l) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length != BodyLength)
        {
            Logger.LogWarning("Session {Id}: 23012 body is {Len} bytes (the client writes {Want})",
                session.SessionId, payload.Length, BodyLength);
            return;
        }
        ushort channelIndex = BinaryPrimitives.ReadUInt16LittleEndian(payload.Span);
        var guid = payload.Span.Slice(HeadLength, GuidLength);

        var channel = CommunityChannels.ByGuid(guid);
        ushort result = Decide(session, channel);
        if (channel is null)
            Logger.LogWarning("Session {Id}: 23012 for channel {Index} with unknown GUID {Guid}; answered {Result}",
                session.SessionId, channelIndex, Convert.ToHexString(guid), result);
        else
        {
            if (channel.Index != channelIndex)
                // The client finds the channel by the GUID alone, so the GUID decides; a different index only
                // means the client's channel record disagrees with the 23534 we sent.
                Logger.LogWarning("Session {Id}: 23012 names index {Index} for the GUID of channel {Channel} (index {Own})",
                    session.SessionId, channelIndex, channel.Name, channel.Index);
            Logger.LogInformation("Session {Id}: {Who} {What} channel {Channel}", session.SessionId, session.Username ?? "?",
                result == ResultJoined ? "joins" : "is already in", channel.Name);
        }

        // Sent unencrypted like 23519/23534: the client does not accept the encrypted path for messenger replies.
        await session.SendInternalRmiAsync(ChatS2COpcodes.AnsPublicChatRoomJoin, BuildAnswer(result, guid), ct);
    }

 /// <summary>
 /// The 23518 result for <paramref name="session"/> asking to join <paramref name="channel"/> (null: the GUID is no
 /// channel), joining it on <see cref="ResultJoined"/>.
 /// </summary>
    public static ushort Decide(ProudSession session, CommunityChannels.Channel? channel)
    {
        if (channel is null) return ResultChannelNotFound;
        return CommunityChannelMembership.Join(session, channel.Index) ? ResultJoined : ResultAlreadyParticipating;
    }

 /// <summary>
 /// The 23518 body, <c>[u16 result][16B channel GUID]</c>, the GUID echoed from the 23012. Layout from the
 /// client's reader alone.
 /// </summary>
    public static byte[] BuildAnswer(ushort result, ReadOnlySpan<byte> channelGuid)
    {
        if (channelGuid.Length != GuidLength)
            throw new ArgumentException($"A channel GUID is {GuidLength} bytes.", nameof(channelGuid));
        var body = new byte[BodyLength];
        BinaryPrimitives.WriteUInt16LittleEndian(body, result);
        channelGuid.CopyTo(body.AsSpan(HeadLength));
        return body;
    }
}
