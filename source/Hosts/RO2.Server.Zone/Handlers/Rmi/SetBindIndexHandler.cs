using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1309 ReqSetBindIndex, which binds the player's respawn point to a save-point NPC.
/// </summary>
/// <remarks>
/// The reply, RMI 2440 OnSavePoint, is a compact-length ByteArray. A body that is not framed this way
/// makes the client dereference a null buffer and crash, so the reply must always be framed.
/// The reply carries no map id or coordinates: the client resolves the bind index against its own save-point data.
/// <para>
/// Request layout:
/// <code>
/// [compactLen 01 0E][u16 innerSize=14][u16 msgTag=58114]
/// [u32 actorHandle][u32 npcHandle][u16 bindIndex]
/// </code>
/// Response layout: <code>[u16 previousBindIndex]</code>
/// </para>
/// <para>
/// Bodies are parsed by their declared length; the handler accepts 4 trailing transport bytes.
/// </para>
/// </remarks>
public class SetBindIndexHandler : ZoneRmiHandlerBase<SetBindIndexHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqSetBindIndex;
    public override string Name => "SetBindIndex";

    /// <summary>msgTag of the 1309 request (compact-length envelope).</summary>
    internal const ushort MsgTagReqSetBindIndex = 58114;

    /// <summary>msgTag of the 2440 reply.</summary>
    internal const ushort MsgTagOnSavePoint = 58115;

    /// <summary>Compact-length width marker for a one-byte length (values below 0x100).</summary>
    private const byte CompactWidth1 = 0x01;

    /// <summary>Length of the compact-length prefix of a one-byte-width envelope: the marker byte plus the length byte.</summary>
    private const int CompactPrefixLen = 2;

    /// <summary>Declared length of the envelope body: [u16 innerSize][u16 msgTag][u32 actorHandle][u32 npcHandle][u16 bindIndex].</summary>
    private const int BindBlobLen = 14;

    /// <summary>Offset of the actor handle inside the envelope body (after innerSize and msgTag).</summary>
    private const int ActorHandleOffset = 4;

    /// <summary>Offset of the bind index inside the envelope body (after the actor and NPC handles).</summary>
    private const int BindIndexOffset = 12;

    /// <summary>Result code of the 2440 reply: 0 = success; a non-zero value makes the client show a system message and abort.</summary>
    private const short ResultSuccess = 0;

    /// <summary>Body length of the 1309 request and the 2440 reply.</summary>
    internal const int BindBodyLen = CompactPrefixLen + BindBlobLen;

    public SetBindIndexHandler(IServiceScopeFactory s, ILogger<SetBindIndexHandler> l,
        PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload,
        PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!TryParseRequest(payload.Span, out var actorHandle, out var bindIndex))
        {
            // Stay silent rather than answer a malformed request: an unframed 2440 crashes the client,
            // so a wrong reply is worse than none.
            Logger.LogWarning("Session {Id}: malformed ReqSetBindIndex ({Len} bytes)",
                session.SessionId, payload.Length);
            return;
        }

        var previousBindIndex = player.BindIndex;
        player.BindIndex = bindIndex;
        // The world-entry snapshot carries the bind too, so every in-world head built from it
        // (2391 OnDeadMe, 2336 OnRepairItem, 2487 OnUpdatePCSaveData) reports the new bind at head+105.
        if (player.Character is { } snapshot)
            snapshot.BindIndex = bindIndex;

        Logger.LogDebug("Session {Id}: SetBindIndex {Prev} -> {Next}",
            session.SessionId, previousBindIndex, bindIndex);

        await session.SendRmiAsync(ZoneS2COpcodes.OnSavePoint,
            w => w.WriteBytes(BuildOnSavePoint2440(actorHandle, ResultSuccess, previousBindIndex, bindIndex)));
    }

    /// <summary>Parses the 1309 request, tolerating the 4 trailing transport bytes.</summary>
    internal static bool TryParseRequest(ReadOnlySpan<byte> body, out uint actorHandle,
        out ushort bindIndex)
    {
        actorHandle = 0;
        bindIndex = 0;
        if (body.Length < BindBodyLen || body[0] != CompactWidth1 || body[1] != BindBlobLen)
            return false;
        var blob = body.Slice(CompactPrefixLen, BindBlobLen);
        actorHandle = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(ActorHandleOffset));
        bindIndex = BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(BindIndexOffset));
        return true;
    }

    /// <summary>Builds the 2440 OnSavePoint reply body.</summary>
    internal static byte[] BuildOnSavePoint2440(uint actorHandle, short result,
        ushort previousBindIndex, ushort bindIndex)
    {
        using var pw = new PacketWriter();
        // WriteCompactBABody writes the envelope: the compact-length marker and length byte (01 0E), then the
        // u16 innerSize (14) at blob+0x00. The lambda writes the rest of the 14-byte blob.
        pw.WriteCompactBABody(blob =>
        {
            blob.WriteUInt16(MsgTagOnSavePoint);    // blob+0x02 msgTag
            blob.WriteUInt32(actorHandle);          // blob+0x04 rider actor handle, echoed from the request
            blob.WriteInt16(result);                // blob+0x08 result: 0 = success
            blob.WriteUInt16(previousBindIndex);    // blob+0x0A bind index before this request
            blob.WriteUInt16(bindIndex);            // blob+0x0C new bind index, echoed from the request
        });
        return pw.ToArray();
    }
}
