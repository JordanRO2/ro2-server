using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1583 (ReqWithdrawUnion): the player leaves its union.</summary>
/// <remarks>
/// Request: fixed 4 bytes, <c>[u32 unionId]</c>.
/// Answer: one 2850 AnsWithdrawUnion carrying a <c>[u16 result]</c>, computed from the stored membership
/// (<see cref="UnionRules.WithdrawResult"/>). A refusal code is returned when the player is not a member
/// of that union or joined it within the last 14 days; otherwise the result is 0.
/// A character with no stored membership is answered 0, which makes the client drop the union it shows.
/// A body too short to carry the id gets no answer.
/// </remarks>
public class WithdrawUnionHandler : ZoneRmiHandlerBase<WithdrawUnionHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqWithdrawUnion;
    public override string Name => "WithdrawUnion";

    public WithdrawUnionHandler(IServiceScopeFactory s, ILogger<WithdrawUnionHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 4)
        {
            Logger.LogWarning("{Name}: ReqWithdrawUnion of {Len} bytes, expected 4 ([u32 unionId]); not answered",
                player.Name, payload.Length);
            return;
        }
        uint unionId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Span);

        var memberships = services.GetRequiredService<RO2.Characters.Domain.IUnionMembershipRepository>();
        var membership = await memberships.GetAsync(player.CharacterId, ct);
        ushort result = UnionRules.WithdrawResult(membership, unionId, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        
        
        bool deleted = result == UnionRules.ResultOk && membership is not null
            && await memberships.WithdrawAsync(player.CharacterId, unionId, ct);

        Logger.LogInformation("{Name} withdraws from union {Union}: result {Result} (stored {Stored}, deleted {Deleted})",
            player.Name, unionId, result, membership is { } m ? $"{m.UnionId} since {m.JoinedAtUnix}" : "none", deleted);

        
        
        if (result == UnionRules.ResultOk) UnionMembershipStore.Publish(player.CharacterId, null);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsWithdrawUnion, writer => writer.WriteUInt16(result));
    }
}
