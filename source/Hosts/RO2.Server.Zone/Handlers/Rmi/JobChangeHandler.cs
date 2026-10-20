using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1408 ReqPCJobChange: parsed and logged; the requested job is not applied.</summary>
/// <remarks>
/// <para>
/// The request is parsed from its envelope: innerSize 14, msgTag 58345, sender handle at blob+4, a u32 argument
/// at blob+8 and a u16 argument at blob+12.
/// </para>
/// <para>
/// The requested job is not applied. Job changes are granted by quest turn-in (see
/// <c>QuestPrizeHandler.ApplyQuestJobChangeAsync</c>), which sends 2481 and 2480. The reply to this request is
/// not known, so accepting the requested job would let the client choose its own class.
/// </para>
/// <para>
/// The 2528 job-change broadcast is not sent: its frame has no envelope, and its consumer stores a mode byte,
/// not the class.
/// </para>
/// </remarks>
public class JobChangeHandler : ZoneRmiHandlerBase<JobChangeHandler>
{
 /// <summary>msgTag of the 1408 request (58345).</summary>
    private const ushort MsgTagReqPCJobChange = 58345;

    public override ushort Opcode => ZoneC2SOpcodes.ReqPCJobChange;
    public override string Name => "JobChange";
    public JobChangeHandler(IServiceScopeFactory s, ILogger<JobChangeHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!PartyAnswerPackets.TryGetCompactBlob(payload.Span, out var blob)
            || !PartyAnswerPackets.TryReadEnvelope(blob, MsgTagReqPCJobChange, 14, out var sender))
        {
            Logger.LogDebug("Session {Id}: malformed ReqPCJobChange ({Len}B)", session.SessionId, payload.Length);
            return Task.CompletedTask;
        }

        var arg8 = BinaryPrimitives.ReadUInt32LittleEndian(blob[8..]);
        var requestedJob = BinaryPrimitives.ReadUInt16LittleEndian(blob[12..]);
        Logger.LogInformation(
            "{Name} (job {Job}) sent ReqPCJobChange: sender {Sender:X8}, u32@blob+8 {Arg8:X8}, u16@blob+12 {Requested}; not applied (job changes come from the quest turn-in)",
            player.Name, player.Job, sender, arg8, requestedJob);
        return Task.CompletedTask;
    }
}
