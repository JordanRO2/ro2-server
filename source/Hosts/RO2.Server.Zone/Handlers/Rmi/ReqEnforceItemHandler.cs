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
/// Handles RMI 1330 (ReqEnforceItem), a legacy item-enforce request the client does not send. The request
/// is logged and not answered.
/// </summary>
/// <remarks>
/// The request body is a compact blob whose layout is not known. Equipment enforcement is handled by 1611
/// (ReqCardEnforceItem), 1663/1664 (ReqRandomEnforceItem) and 1668 (refine). Gear unequip is 1324
/// (ReqPCItemSwap), handled by ItemSwapHandler.
/// </remarks>
public class ReqEnforceItemHandler : ZoneRmiHandlerBase<ReqEnforceItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqEnforceItem;
    public override string Name => "ReqEnforceItem";
    public ReqEnforceItemHandler(IServiceScopeFactory s, ILogger<ReqEnforceItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>Reads the conventional blob header (innerSize, msgTag, actorHandle), or null when the blob is
 /// shorter than 8 bytes.</summary>
    internal static (ushort InnerSize, ushort MsgTag, uint ActorHandle)? ReadConventionalHeader(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < 8) return null;
        return (BinaryPrimitives.ReadUInt16LittleEndian(blob),
                BinaryPrimitives.ReadUInt16LittleEndian(blob[2..]),
                BinaryPrimitives.ReadUInt32LittleEndian(blob[4..]));
    }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!payload.Span.TryGetCompactBlob(CompactLengthCheck.Strict, out var blob))
        {
            Logger.LogWarning("{Name}: 1330 ReqEnforceItem body ({Len} bytes) is not a well-formed VarBlobC; ignored",
                player.Name, payload.Length);
            return Task.CompletedTask;
        }

        if (ReadConventionalHeader(blob) is { } h)
            Logger.LogWarning("{Name}: 1330 ReqEnforceItem (no b303 builder, layout unknown), blob {Len} bytes, innerSize {Inner} msgTag {Tag} actor 0x{Actor:X8}; not answered",
                player.Name, blob.Length, h.InnerSize, h.MsgTag, h.ActorHandle);
        else
            Logger.LogWarning("{Name}: 1330 ReqEnforceItem (no b303 builder, layout unknown), blob {Len} bytes; not answered",
                player.Name, blob.Length);
        return Task.CompletedTask;
    }
}
