using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1670 ReqBeginRefineShop (ByteArray) - Open refine shop</summary>
/// <remarks>
/// <para>
/// Request, 18-byte bodies:
/// <c>[01 10][u16 innerSize=16][u16 msgTag=58509][u32 PC handle@4][u32 value@8][u32 0@12]</c>.
/// blob+8 is 0 or 1; blob+12 is always 0.
/// </para>
/// <para>
/// Reply 2999 AnsBeginRefineShop: its blob+8 equals the request's blob+8. The 3003
/// NfyBeginRefineShop broadcast with the same value is not sent: the client treats it as a no-op.
/// </para>
/// <para>
/// The value is also the shop's refine-service offer, so it is kept per owner and read back with
/// <see cref="TryGetRefineService"/>: by PrivateShopBeginHandler for the 2651/2663 category bit 2
/// (PrivateShopRegistry.CategoryRefineService) and by PrivateShopBuyerOpenHandler for the buyers' 3000
/// blob+8. The client sends a 1670 between its last 1473 and every 1478 ReqBeginPrivateShop, and the answering
/// 2651's category byte (@56) has bit 2 set when the last 1670 carried 1 and clear when it carried 0.
/// On the buyer side, 3000 NfyRefineCommissionToBuyer blob+8 equals the owner's 2663 category bit 2.
/// Because a fresh 1670 precedes every 1478, the kept value is never one from an earlier shop, so unlike
/// the 1674 commission it needs no clearing at 1480.
/// </para>
/// <para>
/// The value is all the owner's requests say about the service: it is the per-step refine price the buyers'
/// 3000 carries at blob+8 (a u64 there, the client listing n x price for n = 1..20). The three u16s after it
/// (blob+16/18/20) come from no request: they are the refine traits the owner is WEARING, Trait_Type 100/101/102
/// (given by the hammers 60000747/60000748), the same three the
/// owner's own 2498 carries at blob+134/136/138. They follow the owner's gear and not
/// this value. PrivateShopBuyerOpenHandler reads them from the owner's worn items when a buyer opens the shop
/// (see RefineCommissionPackets).
/// </para>
/// </remarks>
public class BeginRefineShopHandler : ZoneRmiHandlerBase<BeginRefineShopHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqBeginRefineShop;
    public override string Name => "BeginRefineShop";

 /// <summary>Blob length of a 1670: 16 bytes (msgTag, PC handle, value, reserved zero).</summary>
    private const int BlobLength = 16;

 /// <summary>Request blob offset of the u32 refine-service value (0 = no service offered, 1 = offered).</summary>
    private const int RequestValueOffset = 8;

 /// <summary>The last 1670 blob+8 each shop owner sent, per zone session, weakly keyed like
 /// RandomRefineCommissionHandler's commissions.</summary>
    private static readonly ConditionalWeakTable<PlayerState, StrongBox<uint>> RefineServices = new();

    public BeginRefineShopHandler(IServiceScopeFactory s, ILogger<BeginRefineShopHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>The blob+8 of the last 1670 <paramref name="owner"/> sent, if any: non-zero when the shop it
 /// is setting up offers the refine service (2651/2663 category bit 2, 3000 blob+8; see remarks).</summary>
    internal static bool TryGetRefineService(PlayerState owner, out uint value)
    {
        value = 0;
        if (!RefineServices.TryGetValue(owner, out var box)) return false;
        value = box.Value;
        return true;
    }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        // Payload = [u8 marker][marker-byte length][blob]; the marker is 1 in a 1670.
        // Lenient: the declared length is not compared, only that the blob holds BlobLength bytes.
        var span = payload.Span;
        if (!span.TryGetCompactBlob(CompactLengthCheck.Lenient, out var blob) || blob.Length < BlobLength)
        {
            Logger.LogWarning("Session {Id}: BeginRefineShop body is {Len} bytes, not the 18-byte compact frame retail sends",
                session.SessionId, span.Length);
            return;
        }
        uint requestValue = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(RequestValueOffset, sizeof(uint)));

        RefineServices.AddOrUpdate(player, new StrongBox<uint>(requestValue));
        Logger.LogDebug("{Name} opens refine shop (value {Value})", player.Name, requestValue);

        // AnsBeginRefineShop (2999): compact-length envelope, msgTag 58510, then
        // [u32 actorHandle][u32 the request's blob+8][u32 0]. A raw body without the envelope crashes the client.
        var refineOpenBody = ShopItemPackets.BuildBeginRefineShop2999(player.EntityId, requestValue);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsBeginRefineShop,
            writer => writer.WriteBytes(refineOpenBody));
    }
}
