using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1532 (ReqNextStageLevel): changes the dungeon stage level.</summary>
/// <remarks>
/// <para>
/// Body: one u32, the actor handle of the NPC the player is talking to.
/// </para>
/// <para>
/// No reply is sent. A stage-start notification is not sent either, since it would announce a stage that the
/// server has not started.
/// </para>
/// </remarks>
public class NextStageLevelHandler : ZoneRmiHandlerBase<NextStageLevelHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqNextStageLevel;
    public override string Name => "NextStageLevel";
    public NextStageLevelHandler(IServiceScopeFactory s, ILogger<NextStageLevelHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 4)
        {
            Logger.LogWarning("Session {Id}: malformed ReqNextStageLevel ({Len} bytes)", session.SessionId, payload.Length);
            return Task.CompletedTask;
        }

        var npcHandle = BinaryPrimitives.ReadUInt32LittleEndian(payload.Span);
        Logger.LogInformation("{Name} asked stage NPC {Npc:X8} for the next stage level; stage dungeons are not modelled",
            player.Name, npcHandle);
        return Task.CompletedTask;
    }
}
