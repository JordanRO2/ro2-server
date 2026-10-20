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
/// Decoded fields of a 1332 ReqPCStealItemPickUp request (see <see cref="ItemEnforceHandler"/>).
/// </summary>
public readonly record struct StealItemPickUpRequest(ushort MsgTag, uint ActorHandle, ulong ItemUid, byte Flag, uint TargetHandle);

/// <summary>
/// Handles ReqPCStealItemPickUp: a pickup from the steal window. The request is parsed and logged; no reply is sent.
/// </summary>
/// <remarks>
/// <para>
/// The blob is a compact envelope of 21 bytes: u16 inner size 21, u16 message tag 58374, u32 own actor handle, u64 item uid at offset 8, u8 flag at offset 16, and u32 target handle at offset 17. The shape is the same as 1322 ReqPCItemPickUp.
/// </para>
/// <para>
/// No reply is sent because the steal window's server-to-client messages are not implemented. The class keeps its name because PartyNotifyOutPackets refers to it for the 2360 decode.
/// </para>
/// </remarks>
public class ItemEnforceHandler : ZoneRmiHandlerBase<ItemEnforceHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPCStealItemPickUp;
    public override string Name => "StealItemPickUp";

    public ItemEnforceHandler(IServiceScopeFactory s, ILogger<ItemEnforceHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>
 /// Blob length the builder allocates: 21 bytes.
 /// </summary>
    public const int BlobLength = 21;

 /// <summary>
 /// Decodes a 1332 request body, including the compact prefix. Returns false when the envelope is malformed or the blob is shorter than <see cref="BlobLength"/>.
 /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out StealItemPickUpRequest request)
    {
        request = default;
        if (!payload.TryGetCompactBlob(CompactLengthCheck.Lenient, out var blob) || blob.Length < BlobLength) return false;
        request = new StealItemPickUpRequest(
            MsgTag: BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(2)),
            ActorHandle: BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(4)),
            ItemUid: BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(8)),
            Flag: blob[16],
            TargetHandle: BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(17)));
        return true;
    }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!TryParse(payload.Span, out var req))
        {
            Logger.LogWarning("{Name}: malformed 1332 ({Len} bytes)", player.Name, payload.Length);
            return Task.CompletedTask;
        }
        Logger.LogInformation("{Name} picks stolen item uid {Uid} (flag {Flag}) from handle 0x{Target:X8}; not answered: the steal window's S2C side is dead stubs",
            player.Name, req.ItemUid, req.Flag, req.TargetHandle);

        
        
        
        
        
        
        
        
        
        
        
        
        return Task.CompletedTask;
    }
}
