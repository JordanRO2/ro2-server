using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1361 (ReqSkillTeleport). Logged and ignored; the shipped client does not send it. The payload is not
/// parsed and no teleport is performed.
/// </summary>
public class SkillTeleportHandler : ZoneRmiHandlerBase<SkillTeleportHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqSkillTeleport;
    public override string Name => "SkillTeleport";

    /// <summary>How many leading payload bytes the debug log prints.</summary>
    private const int LoggedHeadBytes = 16;

    public SkillTeleportHandler(IServiceScopeFactory s, ILogger<SkillTeleportHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("{Name}: {Opcode} ReqSkillTeleport body ({Len} bytes) {Head}", player.Name, Opcode, payload.Length,
                Convert.ToHexString(payload.Span[..Math.Min(LoggedHeadBytes, payload.Length)]));
        Logger.LogWarning("{Name}: {Opcode} ReqSkillTeleport ignored; the stock client has no path that sends it",
            player.Name, Opcode);
        return Task.CompletedTask;
    }
}
