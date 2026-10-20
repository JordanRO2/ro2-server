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
/// RMI 1331 ReqPCItemRune. The body layout is not known, so the request is only logged and nothing is answered.
/// </summary>
public class ItemRuneHandler : ZoneRmiHandlerBase<ItemRuneHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPCItemRune;
    public override string Name => "ItemRune";
    public ItemRuneHandler(IServiceScopeFactory s, ILogger<ItemRuneHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>Reads the blob length and, when the blob has at least 4 bytes, its msgTag (at blob+2) from a 1331 body.
 /// Returns false when the compact envelope is malformed.</summary>
    public static bool TryDescribe(ReadOnlySpan<byte> payload, out int blobLength, out ushort? msgTag)
    {
        blobLength = 0;
        msgTag = null;
        if (!payload.TryGetCompactBlob(CompactLengthCheck.Lenient, out var blob)) return false;
        blobLength = blob.Length;
        if (blob.Length >= 4) msgTag = BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(2));
        return true;
    }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (TryDescribe(payload.Span, out var len, out var tag))
            Logger.LogInformation("{Name} sent 1331 (no client builder exists): blob {Len} bytes, msgTag {Tag}",
                player.Name, len, tag?.ToString() ?? "-");
        else
            Logger.LogWarning("{Name}: malformed 1331 ({Len} bytes)", player.Name, payload.Length);
        return Task.CompletedTask;
    }
}
