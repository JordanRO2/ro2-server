using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1316 (ReqPCWhisper) and produces RMI 2325 (OnReceiveWhisper) replies.
/// </summary>
/// <remarks>
/// <para>
/// Request: a VarBlob32 whose blob starts with the standard envelope <c>[u16 innerSize][u16 msgTag]</c>,
/// then the sender handle, the recipient name, and the message. Names and the message are length-prefixed
/// UTF-16 strings. The message ends with a U+F801 terminator unit, which is counted in its length.
/// </para>
/// <para>
/// Reply: a VarBlob32 whose blob holds the envelope (with the 2325 tag), the owner handle, a result code,
/// a flag, the sender name, the recipient name, and the message. Each delivered whisper produces one copy per
/// party, and each copy carries the handle of the client it is addressed to.
/// </para>
/// <para>
/// When the recipient is not online, the sender receives a single 2325 whose result code is
/// <see cref="TargetNotFoundResult"/>.
/// </para>
/// <para>
/// Long messages may arrive zlib-compressed. The transport layer inflates them before dispatch, so this
/// handler always receives the uncompressed body.
/// </para>
/// </remarks>
public class WhisperHandler : ZoneRmiHandlerBase<WhisperHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPCWhisper;
    public override string Name => "Whisper";

 /// <summary>
 /// The 2325 envelope word: innerSize in the low half and msgTag 58015 in the high half, packed as one
 /// u32 to match how the envelope is written on the wire.
 /// </summary>
    private const uint OnReceiveWhisperEnvelope = 58015u << 16;

 /// <summary>Result code in the 2325 sent when the recipient cannot be reached.</summary>
    internal const ushort TargetNotFoundResult = 15703;

    public WhisperHandler(
        IServiceScopeFactory scopeFactory,
        ILogger<WhisperHandler> logger,
        PlayerStateManager playerManager,
        BroadcastService broadcast)
        : base(scopeFactory, logger, playerManager, broadcast)
    {
    }

    protected override async Task HandleAsync(
        ProudSession session,
        ReadOnlyMemory<byte> payload,
        PlayerState player,
        IServiceProvider services,
        CancellationToken ct)
    {
        var span = payload.Span;

        
        
        if (span.Length < 12 + 1 + 2)
            return;

        var reader = new PacketReader(span);
        reader.Skip(4);                       
        reader.Skip(4);                       
        reader.Skip(4);                       

        byte targetChars = reader.ReadByte();
        if (reader.Remaining < targetChars * 2 + 2)
            return;

        
        var targetRaw = reader.ReadBytes(targetChars * 2).ToArray();
        string targetName = Encoding.Unicode.GetString(targetRaw).TrimEnd('\0');

        ushort msgChars = reader.ReadUInt16();
        if (msgChars == 0 || reader.Remaining < msgChars * 2)
            return;

        
        
        var messageRaw = reader.ReadBytes(msgChars * 2).ToArray();
        string messageText = msgChars > 1
            ? Encoding.Unicode.GetString(messageRaw, 0, (msgChars - 1) * 2).TrimEnd('\0')
            : string.Empty;

        Logger.LogInformation("[Whisper] {Sender} → {Target}: {Message}",
            player.Name, targetName, messageText);

        
        
        
        
        var senderNameRaw = Encoding.Unicode.GetBytes(player.Name);

        
        
        
        
        var target = PlayerManager.GetPlayerByName(targetName);
        var targetSession = target is null ? null : Broadcast.GetSession(target.SessionId);
        if (target is null || targetSession is null)
        {
            Logger.LogDebug("Whisper target '{Target}' not online: result {Code} to the sender", targetName,
                TargetNotFoundResult);
            await session.SendInternalRmiAsync(
                ZoneS2COpcodes.OnReceiveWhisper,
                BuildWhisperBlob(OnReceiveWhisperEnvelope, player.EntityId, senderNameRaw, targetRaw, messageRaw,
                    TargetNotFoundResult),
                ct);
            return;
        }

        
        await targetSession.SendInternalRmiAsync(
            ZoneS2COpcodes.OnReceiveWhisper,
            BuildWhisperBlob(OnReceiveWhisperEnvelope, target.EntityId, senderNameRaw, targetRaw, messageRaw),
            ct);

        
        await session.SendInternalRmiAsync(
            ZoneS2COpcodes.OnReceiveWhisper,
            BuildWhisperBlob(OnReceiveWhisperEnvelope, player.EntityId, senderNameRaw, targetRaw, messageRaw),
            ct);
    }

 /// <summary>Builds the S2C 2325 OnReceiveWhisper body (everything after the RMI header bytes).</summary>
 /// <param name="envelopeWord">Packed <c>[u16 innerSize][u16 msgTag]</c> envelope word.</param>
 /// <param name="ownerHandle">Actor handle of the client this copy is sent to.</param>
 /// <param name="senderNameRaw">Sender display name, without a terminator unit.</param>
 /// <param name="recipNameRaw">Recipient name as received.</param>
 /// <param name="messageRaw">Message text, including the trailing U+F801 terminator unit.</param>
 /// <param name="resultCode">0 for delivered copies, or <see cref="TargetNotFoundResult"/> for the not-found answer.</param>
    internal static byte[] BuildWhisperBlob(
        uint envelopeWord,
        uint ownerHandle,
        ReadOnlySpan<byte> senderNameRaw,
        ReadOnlySpan<byte> recipNameRaw,
        ReadOnlySpan<byte> messageRaw,
        ushort resultCode = 0)
    {
        using var w = new PacketWriter();

        
        int blobLen = 11
                    + 1 + senderNameRaw.Length
                    + 1 + recipNameRaw.Length
                    + 2 + messageRaw.Length;

        w.WriteUInt32((uint)blobLen);

        
        
        w.WriteUInt32(envelopeWord);
        w.WriteUInt32(ownerHandle);
        w.WriteUInt16(resultCode);
        w.WriteByte(0);

        w.WriteByte((byte)(senderNameRaw.Length / 2)); 
        w.WriteBytes(senderNameRaw);
        w.WriteByte((byte)(recipNameRaw.Length / 2));  
        w.WriteBytes(recipNameRaw);
        w.WriteUInt16((ushort)(messageRaw.Length / 2)); 
        w.WriteBytes(messageRaw);

        return w.GetWrittenSpan().ToArray();
    }
}
