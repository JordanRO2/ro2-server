using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1477 ReqDeletePrivateShopCraft, which removes a craft recipe from the player's private shop offers.</summary>
/// <remarks>
/// The request body is a u16 craft id. The reply, AnsDeletePrivateShopCraft (2650), carries a u16 result followed by
/// the craft id. The result is 0 when the offer was removed and 23012 when the recipe is not on offer. The offer is
/// removed from the live shop registry and from the character's stored offers, so it is not listed again in later
/// sessions. Requests shorter than the craft id are logged and not answered.
/// </remarks>
public class DeletePrivateShopCraftHandler : ZoneRmiHandlerBase<DeletePrivateShopCraftHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqDeletePrivateShopCraft;
    public override string Name => "DeletePrivateShopCraft";
    public DeletePrivateShopCraftHandler(IServiceScopeFactory s, ILogger<DeletePrivateShopCraftHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>Length of the request body: one u16 craft id.</summary>
    private const int RequestBytes = 2;

 /// <summary>Reply result for an offer that was removed.</summary>
    internal const ushort ResultRemoved = 0;

 /// <summary>Reply result for a recipe that is not on offer.</summary>
    internal const ushort ResultNotOffered = 23012;

 /// <summary>Builds the AnsDeletePrivateShopCraft body: a u16 result followed by a u16 craft id.</summary>
 /// <param name="result">The reply result code.</param>
 /// <param name="craftId">The craft id the reply refers to.</param>
    internal static byte[] BuildAnswer(ushort result, ushort craftId)
    {
        var body = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(body, result);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), craftId);
        return body;
    }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < RequestBytes)
        {
            Logger.LogWarning("Session {Id}: DeletePrivateShopCraft body is {Len} bytes, not the 2 retail sends",
                session.SessionId, payload.Length);
            return;
        }
        ushort craftId = BinaryPrimitives.ReadUInt16LittleEndian(payload.Span);

        var removed = await PrivateShopCraftStore.RemoveAsync(player, craftId, services.GetRequiredService<IPrivateShopCraftRepository>(), ct);
        if (removed)
            Logger.LogInformation("{Name} removes craft {CraftId} from private shop", player.Name, craftId);
        else
            Logger.LogInformation("{Name} removes craft {CraftId} from private shop, which is not on offer; refused with {Code}",
                player.Name, craftId, ResultNotOffered);

        
        await session.SendRmiAsync(ZoneS2COpcodes.AnsDeletePrivateShopCraft,
            BuildAnswer(removed ? ResultRemoved : ResultNotOffered, craftId), ct);
    }
}
