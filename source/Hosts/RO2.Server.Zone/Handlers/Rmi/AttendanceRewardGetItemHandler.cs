using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Network.Protocol;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1645 ReqAttendanceRewardGetItem — claim a daily-attendance reward. Retail does not answer.</summary>
/// <remarks>
/// <para>
/// <b>Request:</b> one u32, the reward id of the attendance window, written with WriteU32. It is 0 in the
/// requests retail receives.
/// </para>
/// <para>
/// <b>Retail sends nothing back.</b> The request is not followed by any answer (no 2947, no item notify, no gift
/// notify), and its sibling poll 1644 ReqAttendanceReward is unanswered the same way. The old handler answered
/// with a fixed 10-byte 2947 - longer than the client's 6-byte
/// <c>[u16 result][u32 id]</c> reader - claiming success for a reward it never granted.
/// </para>
/// <para>
/// <b>What a real claim would need, and why it is not attempted:</b> the Type-0 rows of Attendance.json (Day 1..6
/// rewards) plus per-character attendance state — which day the player is on, what was already claimed, and the
/// reset time — none of which this server keeps (PlayTimeRewardState covers only the Type-1 play-time chain).
/// The client's 2947 consumer erases the u32 from its claimable map, so answering success without
/// that state would let the window hand out the same reward repeatedly. Matching retail's silence is the
/// neutral behaviour.
/// </para>
/// </remarks>
public class AttendanceRewardGetItemHandler : ZoneRmiHandlerBase<AttendanceRewardGetItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqAttendanceRewardGetItem;
    public override string Name => "AttendanceRewardGetItem";
    public AttendanceRewardGetItemHandler(IServiceScopeFactory s, ILogger<AttendanceRewardGetItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        uint attendanceId = payload.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(payload.Span) : 0;
        Logger.LogDebug("{Name} claims attendance reward {Id}: not answered (retail sends no 2947; no attendance state)",
            player.Name, attendanceId);
        return Task.CompletedTask;
    }
}
