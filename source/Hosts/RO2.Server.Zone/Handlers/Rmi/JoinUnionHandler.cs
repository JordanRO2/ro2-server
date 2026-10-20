using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1582 ReqJoinUnion <c>[u32 unionId]</c>: the player joins a union. Answered by 2849 AnsJoinUnion.</summary>
/// <remarks>
/// <para>
/// The union is validated against Union_Info, the stored membership and the level rule. A new membership is stored
/// and reported in the single 2849 reply. The rules are shared with 1583 ReqWithdrawUnion (<see cref="UnionRules"/>).
/// </para>
/// <para>
/// The membership is written to the character head (union id and join time), so it survives a relog
/// (<see cref="UnionMembershipStore"/>).
/// </para>
/// </remarks>
public class JoinUnionHandler : ZoneRmiHandlerBase<JoinUnionHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqJoinUnion;
    public override string Name => "JoinUnion";

    public JoinUnionHandler(IServiceScopeFactory s, ILogger<JoinUnionHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        await UnionRules.AnswerJoinAsync(session, payload, player, services, Logger, ct);

        
        try
        {
            await UnionMembershipStore.PreloadAsync(player.CharacterId, services.GetRequiredService<RO2.Application.Interfaces.IUnionMembershipRepository>(), ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{Name}: could not re-read the union membership for the 2310 head", player.Name);
        }
    }
}
