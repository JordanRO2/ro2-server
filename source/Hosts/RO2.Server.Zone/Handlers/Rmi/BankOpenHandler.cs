using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1341 ReqBankOpen - Open storage/bank</summary>
public class BankOpenHandler : ZoneRmiHandlerBase<BankOpenHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqBankOpen;
    public override string Name => "BankOpen";
    public BankOpenHandler(IServiceScopeFactory s, ILogger<BankOpenHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    /// <summary>How long a bank-open mark lasts (see <see cref="MarkOpen"/>). A defensive window, long enough for
    /// one NPC visit.</summary>
    public static readonly TimeSpan OpenWindow = TimeSpan.FromMinutes(10);

    /// <summary>How far from where the bank was opened a 1346 is still accepted. The bank NPC's interaction radius
    /// is in no data table, so this reuses the generous NPC reach.</summary>
    public const float MaxRange = ActorGate.NpcInteractReach;

    /// <summary>
    /// Marks the bank open for <paramref name="player"/> at its current position, for <see cref="OpenWindow"/>. The 1346
    /// handler refuses a move without this mark. The remote-bank scroll (ItemUseHandler) calls it too: that path opens
    /// the window with a 2864 and never sends a 1341.
    /// </summary>
    public static void MarkOpen(PlayerState player) =>
        player.MarkBankOpen(DateTime.UtcNow + OpenWindow, player.X, player.Y);

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogDebug("{Name} opens bank", player.Name);
        // INV-H3: the per-session bank-open mark 1346 requires. The request carries no NPC handle,
        // so the range anchor is the player's own position.
        MarkOpen(player);

        // No 2339 OnBankOpen is sent: its body layout is not modelled and a raw body crashes the client
        // (non-compact first byte). TODO: model + WriteCompactBABody.
        await Task.CompletedTask;
    }
}
