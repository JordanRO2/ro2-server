using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Characters.Domain;
using RO2.Characters.Application;

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

        // The flow reads the membership, deletes it when the withdraw is accepted, and clears the head mirror on
        // success. The host keeps the id read, the log (after the mirror clear) and the 2850 reply.
        var repo = services.GetRequiredService<IUnionMembershipRepository>();
        var publisher = services.GetRequiredService<IUnionMembershipPublisher>();
        var (outcome, deleted, stored) = await WithdrawUnionFlow.WithdrawAsync(
            repo, publisher, player.CharacterId, unionId, TimeProvider.System, ct);
        ushort result = UnionRules.Code(outcome);

        Logger.LogInformation("{Name} withdraws from union {Union}: result {Result} (stored {Stored}, deleted {Deleted})",
            player.Name, unionId, result, stored is { } m ? $"{m.UnionId} since {m.JoinedAtUnix}" : "none", deleted);

        await session.SendRmiAsync(ZoneS2COpcodes.AnsWithdrawUnion, writer => writer.WriteUInt16(result));
    }
}
