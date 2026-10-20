using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Network.Protocol;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1674 (ReqRandomRefineCommission): records the random-refine commission a player sets
/// while preparing a refine shop. The value is not answered to the sender.
/// </summary>
/// <remarks>
/// The request carries the player handle and the commission value. The owner's zone session keeps the
/// value until the shop ends, so buyers opening the shop can read it back with <see cref="TryGetCommission"/>.
/// Nothing is charged or paid from it on this server.
/// </remarks>
public class RandomRefineCommissionHandler : ZoneRmiHandlerBase<RandomRefineCommissionHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqRandomRefineCommission;
    public override string Name => "RandomRefineCommission";

 /// <summary>Expected length of the request body.</summary>
    private const int BlobLength = 16;

 /// <summary>The last commission each shop owner set, per zone session.</summary>
    private static readonly ConditionalWeakTable<PlayerState, StrongBox<uint>> Commissions = new();

    public RandomRefineCommissionHandler(IServiceScopeFactory s, ILogger<RandomRefineCommissionHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>
 /// Returns the random-refine commission last set by <paramref name="owner"/>, if any.
 /// </summary>
    internal static bool TryGetCommission(PlayerState owner, out uint commission)
    {
        commission = 0;
        if (!Commissions.TryGetValue(owner, out var box)) return false;
        commission = box.Value;
        return true;
    }

 /// <summary>
 /// Clears the commission of <paramref name="owner"/> when their shop ends, so the next shop starts with none.
 /// </summary>
    internal static void EndShop(PlayerState owner) => Commissions.Remove(owner);

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        var span = payload.Span;
        if (!span.TryGetCompactBlob(CompactLengthCheck.Lenient, out var blob) || blob.Length < BlobLength)
        {
            Logger.LogWarning("Session {Id}: RandomRefineCommission body is {Len} bytes, not the 18-byte compact frame retail sends",
                session.SessionId, span.Length);
            return Task.CompletedTask;
        }
        uint commission = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(8, 4));

        Commissions.AddOrUpdate(player, new StrongBox<uint>(commission));
        Logger.LogDebug("{Name} set random-refine commission {Value}", player.Name, commission);

        
        return Task.CompletedTask;
    }
}
