using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Network.Handlers;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

namespace RO2.Server.Chat.Handlers.Rmi;

/// <summary>
/// RMI 23019 ReqPublicChatRoomColor: the player recolours one community-channel tab.
///
/// C2S body, fixed 6 bytes: <c>[u16 channelIndex][u32 ARGB]</c>, sent when the player applies a colour in the
/// messenger colour window; e.g. channel 12 with FF008000.
///
/// What the server does with it: store the colour for the character, because the tab list sends it back in the
/// 23513 tab list at every later login (see <see cref="IChannelColorRepository"/>).
///
/// Reply: 23530 AnsPublicChatRoomColor to the requester, <c>[u16 result 0][u16 channelIndex][u32 ARGB]</c>, the
/// request's own two fields echoed.
/// <list type="bullet">
/// <item>Layout: NONE family, fixed 8 bytes, read as U16, U16, U32.</item>
/// <item>Client behaviour: a non-zero result shows a result-code message. On 0 the client finds the community
/// channel with the given index, stores the colour on it and refreshes the channel tab.</item>
/// <item>The client already applies the new colour to the tab before sending 23019, so the success echo has no
/// visible effect; it completes the request/answer pair.</item>
/// </list>
/// The requests this handler drops (wrong length, a channel that is not a community channel, before the messenger
/// login) get no answer: no failure code is defined for them.
/// </summary>
public class ReqPublicRoomColorHandler : ChatRmiHandlerBase<ReqPublicRoomColorHandler>
{
    public override ushort Opcode => ChatC2SOpcodes.ReqPublicChatRoomColor; // 23019
    public override string Name => "PublicRoomColor";

    /// <summary>23019 body size: <c>[u16 channelIndex][u32 ARGB]</c>.</summary>
    internal const int RequestLength = 6;

    /// <summary>23019 body+2: the u32 ARGB colour; the u16 channel index is at +0.</summary>
    internal const int ArgbOffset = 2;

    /// <summary>23530 body size: <c>[u16 result][u16 channelIndex][u32 ARGB]</c>.</summary>
    internal const int AnswerLength = 8;

    /// <summary>23530 body+2: the echoed u16 channel index; the u16 result is at +0.</summary>
    internal const int AnswerChannelOffset = 2;

    /// <summary>23530 body+4: the echoed u32 ARGB colour.</summary>
    internal const int AnswerArgbOffset = 4;

    public ReqPublicRoomColorHandler(IServiceScopeFactory s, ILogger<ReqPublicRoomColorHandler> l) : base(s, l) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length != RequestLength)
        {
            Logger.LogWarning("Session {Id}: 23019 body is {Len} bytes (retail: 6)", session.SessionId, payload.Length);
            return;
        }
        ushort channelIndex = BinaryPrimitives.ReadUInt16LittleEndian(payload.Span);
        uint argb = BinaryPrimitives.ReadUInt32LittleEndian(payload.Span.Slice(ArgbOffset));

        // Only the community channels have a colour slot in 23513; private rooms (index 15) are not listed there.
        if (!CommunityChannels.All.Any(c => c.Index == channelIndex))
        {
            Logger.LogWarning("Session {Id}: 23019 for channel {Index}, which is not a community channel",
                session.SessionId, channelIndex);
            return;
        }
        if (session.CharacterId is not uint ownerId)
        {
            Logger.LogWarning("Session {Id}: 23019 before the messenger login, colour not stored", session.SessionId);
            return;
        }

        await services.GetRequiredService<IChannelColorRepository>().SetColorAsync(ownerId, channelIndex, argb, ct);
        Logger.LogInformation("Session {Id}: {Who} sets channel {Index} colour to {Argb:X8}",
            session.SessionId, session.Username ?? "?", channelIndex, argb);

        // Stored, so the answer is the success echo (class remarks), to the requester only.
        await session.SendInternalRmiAsync(ChatS2COpcodes.AnsPublicChatRoomColor,
            BuildAnsPublicChatRoomColor23530(ResultOk, channelIndex, argb), ct);
    }

 /// <summary>23530 result the client treats as success (it stores the colour only on 0).</summary>
    internal const ushort ResultOk = 0;

 /// <summary>23530 body: <c>[u16 result][u16 channelIndex][u32 ARGB]</c>, 8 bytes.</summary>
    internal static byte[] BuildAnsPublicChatRoomColor23530(ushort result, ushort channelIndex, uint argb)
    {
        var body = new byte[AnswerLength];
        BinaryPrimitives.WriteUInt16LittleEndian(body, result);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(AnswerChannelOffset), channelIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(AnswerArgbOffset), argb);
        return body;
    }
}
