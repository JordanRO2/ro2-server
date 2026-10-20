using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1692 ReqShowAttendanceState (empty body, sent from the HUD menu) and opens the attendance calendar.
/// </summary>
/// <remarks>
/// The reply carries the reward calendar and the account's attendance state. These come from the same record that
/// the daily attendance check updates on the account's first map load of each server day, so the calendar,
/// stamps and milestone rewards match that day's popup and the rewards in the gift box.
/// <para>
/// The attendance day is floored at 1, because the client cannot display day 0 (an account with no attendance yet).
/// </para>
/// <para>
/// If the gift box cannot be read, no reply is sent and the client does not open the window.
/// </para>
/// </remarks>
public class ShowAttendanceStateHandler : ZoneRmiHandlerBase<ShowAttendanceStateHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqShowAttendanceState;
    public override string Name => "ShowAttendanceState";

    public ShowAttendanceStateHandler(IServiceScopeFactory s, ILogger<ShowAttendanceStateHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        int attendedDays = 0, streakBrokenAfter = 0;
        if (player.AccountId != 0)
        {
            try
            {
                var gifts = services.GetRequiredService<IAccountGiftRepository>();
                (attendedDays, _, streakBrokenAfter) = await gifts.GetAttendanceAsync(player.AccountId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogWarning(ex, "{Name}: 1692 not answered, the attendance of account {Account} could not be read",
                    player.Name, player.AccountId);
                return;
            }
        }

        await session.SendRmiAsync(ZoneS2COpcodes.AnsShowAttendanceState,
            writer => writer.WriteBytes(EconomyMiscPackets.BuildShowAttendanceState(attendedDays, streakBrokenAfter)), ct);
    }
}
