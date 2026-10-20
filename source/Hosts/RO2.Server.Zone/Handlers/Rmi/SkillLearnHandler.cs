using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1366 (ReqPCSkillLearn). Logged and ignored: the shipped client does not send it. Skills are learned
/// through 1310 ReqUseTalentPoint, answered with 2543 (UseTalentPointHandler).
/// </summary>
/// <remarks>
/// Nothing in the payload is parsed, changed or sent. Acting on it would let a modified client learn skills
/// without spending talent points.
/// </remarks>
public class SkillLearnHandler : ZoneRmiHandlerBase<SkillLearnHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPCSkillLearn;
    public override string Name => "SkillLearn";

    public SkillLearnHandler(IServiceScopeFactory s, ILogger<SkillLearnHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override Task HandleAsync(
        ProudSession session,
        ReadOnlyMemory<byte> payload,
        PlayerState player,
        IServiceProvider services,
        CancellationToken ct)
    {
        Logger.LogWarning("{Name}: 1366 ReqPCSkillLearn of {Len} bytes ignored; 1366 has no sender in the shipped client, " +
            "skills are learned through 1310 ReqUseTalentPoint", player.Name, payload.Length);
        return Task.CompletedTask;
    }
}
