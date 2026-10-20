using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1329 ReqEnforceProbability. The request is logged and never answered.</summary>
/// <remarks>
/// The stock client never sends this request and discards the reply that would answer it, so no reply is produced.
/// The body is logged by length and not parsed.
/// </remarks>
public class EnforceProbabilityHandler : ZoneRmiHandlerBase<EnforceProbabilityHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqEnforceProbability;
    public override string Name => "EnforceProbability";
    public EnforceProbabilityHandler(IServiceScopeFactory s, ILogger<EnforceProbabilityHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Span.TryGetCompactBlob(CompactLengthCheck.Lenient, out var blob))
            Logger.LogDebug("{Name}: 1329 with a {Len}-byte blob; the b303 client has no builder for it, not answered",
                player.Name, blob.Length);
        else
            Logger.LogWarning("{Name}: 1329 body of {Len} bytes is not a compact ByteArray; the b303 client has no builder for it, not answered",
                player.Name, payload.Length);
        return Task.CompletedTask;
    }
}
