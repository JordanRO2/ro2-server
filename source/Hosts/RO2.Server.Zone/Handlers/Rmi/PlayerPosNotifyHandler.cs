using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1465 NfyPlayerPos: logged and ignored, because the stock client never sends it.</summary>
/// <remarks>
/// Only a modified client could reach this handler, and it used to take a client-claimed position. Nothing is
/// parsed, moved or sent (the SkillTeleportHandler pattern); movement comes in through the validated 1526 path.
/// </remarks>
public class PlayerPosNotifyHandler : ZoneRmiHandlerBase<PlayerPosNotifyHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.NfyPlayerPos;
    public override string Name => "PlayerPosNotify";

    public PlayerPosNotifyHandler(IServiceScopeFactory s, ILogger<PlayerPosNotifyHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogWarning("{Name}: 1465 NfyPlayerPos ignored; no live client sender exists (0 frames, HSV MV-5)", player.Name);
        return Task.CompletedTask;
    }
}
