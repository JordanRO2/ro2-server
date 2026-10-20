using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1362 ReqCastingStart: logged, never answered.</summary>
/// <remarks>
/// <para>
/// No b303 client path sends it. Its body is one VarBlobC. The live casting
/// path is 1508/1509 (SkillCastingStartHandler).
/// </para>
/// <para>
/// The answer this handler used to broadcast, 2411 OnSkillCastingNotify, is dead in the client too: its stub
/// is <c>xor al,al</c> (layer2, client_sendable false), and nothing emits it. The 12 raw
/// bytes we sent (<c>[u32 entity][u32 skill][u32 target]</c>) were not even a valid VarBlobC body, whose first byte
/// must be a compact-length marker. So a 1362, which only a modified client could send, gets nothing back,
/// and nothing on this server emits 2411.
/// </para>
/// </remarks>
public class CastingStartHandler : ZoneRmiHandlerBase<CastingStartHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqCastingStart;
    public override string Name => "CastingStart";

    public CastingStartHandler(IServiceScopeFactory s, ILogger<CastingStartHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogDebug("{Name}: 1362 ReqCastingStart ({Len} B: {Hex}) not answered; no b303 client builds it",
            player.Name, payload.Length, Convert.ToHexString(payload.Span[..Math.Min(payload.Length, 64)]));
        return Task.CompletedTask;
    }
}
