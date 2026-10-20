using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1368 ReqEffectList, which requests the effect list of a target entity.</summary>
/// <remarks>
/// The request body is a u32 target entity id. The reply, OnGetEffectList (2491), is always an empty list. The
/// client's reader discards the records, and their layout has no known meaning, so no effects are reported.
/// </remarks>
public class EffectListHandler : ZoneRmiHandlerBase<EffectListHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqEffectList;
    public override string Name => "EffectList";

 /// <summary>Blob length of an empty OnGetEffectList body: a u32 target and a u16 count.</summary>
    internal const uint EmptyBlobLength = 6;

    public EffectListHandler(IServiceScopeFactory s, ILogger<EffectListHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>Builds the OnGetEffectList body for a target with no effect records.</summary>
 /// <param name="targetEntity">The target entity id to echo.</param>
    internal static byte[] BuildEmptyEffectList(uint targetEntity)
    {
        var b = new byte[4 + EmptyBlobLength];
        BinaryPrimitives.WriteUInt32LittleEndian(b, EmptyBlobLength);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), targetEntity);
        
        return b;
    }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 4) return;
        var targetEntity = BinaryPrimitives.ReadUInt32LittleEndian(payload.Span);

        Logger.LogDebug("{Name} requests the effect list of entity 0x{Target:X8}; answered empty (the client's 2491 stub discards it)",
            player.Name, targetEntity);

        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnGetEffectList, BuildEmptyEffectList(targetEntity), ct);
    }
}
