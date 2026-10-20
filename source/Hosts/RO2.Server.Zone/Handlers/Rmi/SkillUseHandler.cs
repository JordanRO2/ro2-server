using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1364 (ReqSkill). Logged and ignored; the shipped client does not send it. Skills are cast through
/// 1508 (SkillStartHandler) and 1509 (SkillDamageHandler). The payload is not parsed, changed or sent.
/// </summary>
public class SkillUseHandler : ZoneRmiHandlerBase<SkillUseHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqSkill;
    public override string Name => "SkillUse";

    public SkillUseHandler(IServiceScopeFactory s, ILogger<SkillUseHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override Task HandleAsync(
        ProudSession session,
        ReadOnlyMemory<byte> payload,
        PlayerState player,
        IServiceProvider services,
        CancellationToken ct)
    {
        Logger.LogDebug("{Name}: 1364 ReqSkill of {Len} bytes ignored; the b303 client has no builder for it (skills are cast through 1508/1509)",
            player.Name, payload.Length);
        return Task.CompletedTask;
    }
}
