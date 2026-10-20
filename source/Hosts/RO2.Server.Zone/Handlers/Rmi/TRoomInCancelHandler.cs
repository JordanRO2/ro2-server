using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1661 (ReqTRoomInCancel). Cancels the Training Room registration made by 1660 (ReqTRoomIn); see
/// <see cref="TrainingRoomQueue"/>. The body is empty and no reply is sent.
/// </summary>
/// <remarks>
/// The requester's registration, or the registration of the party it belongs to, is removed. Other party members
/// keep their registration until they cancel it themselves.
/// </remarks>
public class TRoomInCancelHandler : ZoneRmiHandlerBase<TRoomInCancelHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqTRoomInCancel;
    public override string Name => "TRoomInCancel";
    public TRoomInCancelHandler(IServiceScopeFactory s, ILogger<TRoomInCancelHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var removed = TrainingRoomQueue.Shared.Cancel(player, services.GetRequiredService<PartyService>());
        Logger.LogInformation("{Name} cancels the Training Room registration ({What}); no reply is sent",
            player.Name, removed is null ? "none was held" : $"registered by {removed.Registrant.Name}");
        return Task.CompletedTask;
    }
}
