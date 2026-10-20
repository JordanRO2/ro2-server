using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1439 ReqKharaTitleSelectTimeReset - Reset Khara title selection cooldown</summary>
/// <remarks>
/// 1439 cannot be reached from the client: nothing in the client ever sends it. The
/// 2-byte [u16 result] answer 2583 is read by the client as follows: 0 zeroes the title-select timer, non-zero
/// does nothing. Missing system: a persisted per-character Khara title state with a select-timer expiry.
/// <para>
/// The answer is therefore NON-ZERO (<see cref="ResultNotApplied"/>): a forged 1439 must not zero the client's
/// title-select timer, which the server's own timer (<see cref="KharaTitleStore"/>) never agreed to reset.
/// The refusal value is a design choice; the client only tests for zero / non-zero.
/// </para>
/// </remarks>
public class KharaTitleSelectTimeResetHandler : ZoneRmiHandlerBase<KharaTitleSelectTimeResetHandler>
{
    /// <summary>2583 [u16 result] that leaves the client's timer alone (any non-zero value does).
    /// The value is borrowed from the 2582 refusal code, which fits a u16.</summary>
    internal const ushort ResultNotApplied = (ushort)KharaTitleStore.ResultRefused;

    public override ushort Opcode => ZoneC2SOpcodes.ReqKharaTitleSelectTimeReset;
    public override string Name => "KharaTitleSelectTimeReset";
    public KharaTitleSelectTimeResetHandler(IServiceScopeFactory s, ILogger<KharaTitleSelectTimeResetHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogWarning("{Name}: 1439 Khara title cooldown reset requested (unreachable from the client); refused", player.Name);
        // AnsKharaTitleSelectTimeRest (2583): non-zero, so the client does not zero its timer
        await session.SendRmiAsync(ZoneS2COpcodes.AnsKharaTitleSelectTimeRest, writer =>
        {
            writer.WriteUInt16(ResultNotApplied);
        });
    }
}
