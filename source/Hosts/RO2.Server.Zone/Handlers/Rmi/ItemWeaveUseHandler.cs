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
/// RMI 1333 ReqItemWeaveUse. The stock client does not send this request, so it is parsed and logged but not answered.
/// </summary>
/// <remarks>
/// <para>
/// The body is a compact-length blob of 34 bytes: u16 innerSize 34 at blob+0, u16 msgTag 58134 at blob+2, the sender's
/// actor handle at blob+4, then the request record, whose fields are named here for their record offsets (S+0x..)
/// rather than for a meaning. This server has no weaving mechanic, so no reply is sent.
/// </para>
/// </remarks>
public class ItemWeaveUseHandler : ZoneRmiHandlerBase<ItemWeaveUseHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqItemWeaveUse;
    public override string Name => "ItemWeaveUse";
    public ItemWeaveUseHandler(IServiceScopeFactory s, ILogger<ItemWeaveUseHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    /// <summary>Blob size, also written as innerSize.</summary>
    internal const int BlobSize = 34;

    /// <summary>Message tag at blob+2 (58134).</summary>
    internal const ushort MsgTag = 58134;

    /// <summary>Blob+2: u16 msgTag.</summary>
    private const int MsgTagOffset = 2;

    /// <summary>Blob+4: u32 handle of the sending actor.</summary>
    private const int ActorHandleOffset = 4;

    /// <summary>Blob+8: u8 record field S+0x01.</summary>
    private const int S01Offset = 8;

    /// <summary>Blob+9: u32 record field S+0x08.</summary>
    private const int S08Offset = 9;

    /// <summary>Blob+13: u32 record field S+0x0C.</summary>
    private const int S0COffset = 13;

    /// <summary>Blob+17: u8 record field S+0x10.</summary>
    private const int S10Offset = 17;

    /// <summary>Blob+18: u32 record field S+0x18.</summary>
    private const int S18Offset = 18;

    /// <summary>Blob+22: u32 record field S+0x1C.</summary>
    private const int S1COffset = 22;

    /// <summary>Blob+26: u8 record field S+0x00.</summary>
    private const int S00Offset = 26;

    /// <summary>Blob+27: u8 record field S+0x20.</summary>
    private const int S20Offset = 27;

    /// <summary>Blob+28: u8 record field S+0x21.</summary>
    private const int S21Offset = 28;

    /// <summary>Blob+29: u8 record field S+0x22.</summary>
    private const int S22Offset = 29;

    /// <summary>Blob+30: u16 record field S+0x24.</summary>
    private const int S24Offset = 30;

    /// <summary>Blob+32: u16 record field S+0x26.</summary>
    private const int S26Offset = 32;

    /// <summary>The 1333 request blob. Fields follow the offsets of the request record.</summary>
    internal readonly record struct WeaveUseRequest(
        uint ActorHandle,
        byte S01, uint S08, uint S0C, byte S10, uint S18, uint S1C,
        byte S00, byte S20, byte S21, byte S22, ushort S24, ushort S26);

    /// <summary>Reads the blob fields. Returns false unless the blob is 34 bytes with innerSize 34 and msgTag 58134.</summary>
    internal static bool TryParse(ReadOnlySpan<byte> blob, out WeaveUseRequest request)
    {
        request = default;
        if (blob.Length != BlobSize) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(blob) != BlobSize) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(blob[MsgTagOffset..]) != MsgTag) return false;
        request = new WeaveUseRequest(
            ActorHandle: BinaryPrimitives.ReadUInt32LittleEndian(blob[ActorHandleOffset..]),
            S01: blob[S01Offset],
            S08: BinaryPrimitives.ReadUInt32LittleEndian(blob[S08Offset..]),
            S0C: BinaryPrimitives.ReadUInt32LittleEndian(blob[S0COffset..]),
            S10: blob[S10Offset],
            S18: BinaryPrimitives.ReadUInt32LittleEndian(blob[S18Offset..]),
            S1C: BinaryPrimitives.ReadUInt32LittleEndian(blob[S1COffset..]),
            S00: blob[S00Offset],
            S20: blob[S20Offset],
            S21: blob[S21Offset],
            S22: blob[S22Offset],
            S24: BinaryPrimitives.ReadUInt16LittleEndian(blob[S24Offset..]),
            S26: BinaryPrimitives.ReadUInt16LittleEndian(blob[S26Offset..]));
        return true;
    }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!payload.Span.TryGetCompactBlob(CompactLengthCheck.Strict, out var blob) || !TryParse(blob, out var r))
        {
            Logger.LogWarning("{Name}: 1333 ReqItemWeaveUse body ({Len} bytes) does not match the 34-byte request blob; ignored",
                player.Name, payload.Length);
            return Task.CompletedTask;
        }

        Logger.LogWarning(
            "{Name}: 1333 ReqItemWeaveUse (the stock client never sends it) actor 0x{Actor:X8} S+1={S01} S+8={S08:X8} S+C={S0C:X8} S+10={S10} S+18={S18:X8} S+1C={S1C:X8} S+0={S00} S+20={S20} S+21={S21} S+22={S22} S+24={S24} S+26={S26}; no weaving system, not answered",
            player.Name, r.ActorHandle, r.S01, r.S08, r.S0C, r.S10, r.S18, r.S1C, r.S00, r.S20, r.S21, r.S22, r.S24, r.S26);
        return Task.CompletedTask;
    }
}
