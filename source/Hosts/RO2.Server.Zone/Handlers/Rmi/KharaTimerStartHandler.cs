using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1436 ReqKharaTimerStart: <c>[u32 kharaId][u32 unused]</c>. Answered with 2580 <c>[u32 kharaId]</c> for an
/// accepted timed Khara.
/// </summary>
/// <remarks>
/// <para>
/// The second u32 is not used. The reply is sent only for an accepted timed Khara.
/// </para>
/// <para>
/// The server sends 2580 on its own right after the 2574 of a Khara that starts a timer (see
/// <see cref="KharaEngine.Accept"/>). Timer restarts on engagement are not modelled: they need the timed-kill
/// completion condition and the timer expiry, which KharaEngine does not evaluate.
/// </para>
/// </remarks>
public class KharaTimerStartHandler : ZoneRmiHandlerBase<KharaTimerStartHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqKharaTimerStart;
    public override string Name => "KharaTimerStart";
    public KharaTimerStartHandler(IServiceScopeFactory s, ILogger<KharaTimerStartHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>Reads the Khara id of a 1436 body (u32 at offset 0). Returns false when the body is shorter than 4 bytes.</summary>
    public static bool TryReadKharaId(ReadOnlySpan<byte> payload, out uint kharaId)
    {
        kharaId = 0;
        if (payload.Length < 4) return false;
        kharaId = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        return true;
    }

 /// <summary>
 /// Whether a 2580 answers a 1436 for <paramref name="kharaId"/>: the character holds the Khara accepted and it is a
 /// timed Khara (<see cref="KharaService.IsTimed"/>).
 /// </summary>
    public static bool ShouldAnswer(uint kharaId, IReadOnlyList<uint> activeIds, KharaCatalog catalog)
        => activeIds.Contains(kharaId) && catalog.Get(kharaId) is { } def && KharaService.IsTimed(def);

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!TryReadKharaId(payload.Span, out var kharaId)) return;

        var catalog = KharaCatalog.For(services.GetRequiredService<GameDataStore>());
        if (!ShouldAnswer(kharaId, KharaService.PublishedActiveIds(player.CharacterId), catalog))
        {
            Logger.LogInformation("{Name}: 1436 for Khara {Id} ignored (not an accepted timed Khara)", player.Name, kharaId);
            return;
        }

        Logger.LogInformation("{Name}: 1436 starts the timer of Khara {Id}", player.Name, kharaId);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsKharaTimerStart, KharaService.BuildAnsKharaTimerStart2580(kharaId), ct);
    }
}
