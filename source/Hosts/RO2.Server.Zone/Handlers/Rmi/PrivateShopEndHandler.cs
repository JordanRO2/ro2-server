using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1480 ReqEndPrivateShop: closes the sender's private shop.</summary>
/// <remarks>
/// <para>
/// The answer is 2653 AnsEndPrivateShop, a raw <c>u16 0</c> (success). The seller and the nearby players get 2664
/// NfyEndPrivateShop, and each player who opened the shop gets 2667 NfyTerminatePrivateShopForBuyer. The seller is
/// then put back to the normal state with 2398 OnChangeState, after the other clients' messages.
/// </para>
/// <para>
/// 2664 is a compact-length message carrying the shared 61-byte private-shop struct:
/// <c>[u16 innerSize 61][u16 msgTag 58418][u32 handle@4][u16 0@8][u32 handle@10][title@14][u8 0@56][u32 0@57]</c>.
/// The client reads only the owner handle at +10. The title is the shop's current name (the registry follows renames),
/// sent for completeness. It also goes to the seller, because BroadcastToNearbyAsync skips the sender.
/// </para>
/// <para>
/// 2667 is a raw 6-byte body, <c>[u16 23023][u32 owner handle]</c>. It goes to the players on the shop's viewer list
/// (see PrivateShopRegistry.AddViewer), not to the 2664 audience, so there is no range check.
/// </para>
/// <para>
/// 2398 OnChangeState returns the seller to state 0 (PcNormal). Its trailing words are the ones the shop-begin 2398
/// carries (PrivateShopBeginHandler), from one definition in PrivateShopRegistry.
/// </para>
/// <para>
/// 2605 OnNfyCraftSaleCloseShop is not sent: its client handler does nothing.
/// Only 1480 ends a shop on this server, so nothing else sends 2664, 2667 or the shop-end 2398.
/// The shop's random refine commission is dropped as well, since it lasts for one shop only.
/// </para>
/// </remarks>
public class PrivateShopEndHandler : ZoneRmiHandlerBase<PrivateShopEndHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqEndPrivateShop;
    public override string Name => "PrivateShopEnd";
    public PrivateShopEndHandler(IServiceScopeFactory s, ILogger<PrivateShopEndHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    /// <summary>msgTag of the 2664 NfyEndPrivateShop notification (58418).</summary>
    private const ushort MsgTagNfyEndPrivateShop = 0xE432;

    /// <summary>Leading u16 of the 2667 buyer notification (23023, 0x59EF). The client does not read it; it is sent
    /// as a fixed constant.</summary>
    private const ushort TerminateForBuyerWord = 23023;

    /// <summary>Result code of 2653 AnsEndPrivateShop: success.</summary>
    private const ushort ResultSuccess = 0;

    /// <summary>Capacity of the buffer for the 2664 message: 2 length bytes plus the 61-byte struct, rounded up.</summary>
    private const int NfyBufferCapacity = 64;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogInformation("{Name} ends private shop", player.Name);

        // The random refine commission (1674) ends with the shop: kept, it would put its flag on the next shop's
        // sign. Done first so that a send failing below does not leave it behind.
        RandomRefineCommissionHandler.EndShop(player);

        // 2653 AnsEndPrivateShop.
        await session.SendRmiAsync(ZoneS2COpcodes.AnsEndPrivateShop, writer =>
        {
            writer.WriteUInt16(ResultSuccess);
        });

        // 2664 NfyEndPrivateShop is a compact-length message: a raw body would start with a byte the compact reader
        // takes as its length marker, so it goes through WriteCompactBABody. Built once, then sent to the owner and
        // to the players around it.
        var endHandle = player.EntityId;
        var title = PrivateShopRegistry.TryGet(player, out var name42, out _) ? name42 : new byte[PrivateShopRegistry.NameBytes];
        using var pw = new PacketWriter(NfyBufferCapacity);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagNfyEndPrivateShop);   // blob+2  msgTag
            p.WriteUInt32(endHandle);   // blob+4  owner handle, equal to blob+10
            p.WriteUInt16(0);           // blob+8  result, unused in the notify
            p.WriteUInt32(endHandle);   // blob+10 owner handle, the one the client reads
            p.WriteBytes(title);        // blob+14 shop title, wchar_t[21]
            p.WriteByte(0);             // blob+56 shop category, not carried by 2664
            p.WriteUInt32(0);           // blob+57 unused
        });
        var nfy = pw.ToArray();
        await session.SendRmiAsync(ZoneS2COpcodes.NfyEndPrivateShop, nfy);
        await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.NfyEndPrivateShop, w => w.WriteBytes(nfy));

        // 2667 NfyTerminatePrivateShopForBuyer, to everyone who opened the shop (1481) since it began (1478)
        // and is still in this zone as the same character. The registry never lists the owner.
        foreach (var (viewerSessionId, viewerCharacterId) in PrivateShopRegistry.TakeViewers(player))
        {
            var viewer = PlayerManager.GetPlayer(viewerSessionId);
            if (viewer == null || viewer.CharacterId != viewerCharacterId || ReferenceEquals(viewer, player)) continue;
            var viewerSession = Broadcast.GetSession(viewerSessionId);
            if (viewerSession == null) continue;
            await viewerSession.SendRmiAsync(ZoneS2COpcodes.NfyTerminatePrivateShopForBuyer, w =>
            {
                w.WriteUInt16(TerminateForBuyerWord);
                w.WriteUInt32(endHandle);   // the shop owner's handle
            });
        }

        // 2398 OnChangeState, the seller back to state 0, last: after the seller's 2664, and after the 2664 and any
        // 2667 on the other clients. The shop constant that sits at blob+23 goes through the builder's targetHandle
        // slot because that is where it lies; it is not a handle. Both trailing words are the ones the shop-begin
        // 2398 carries.
        var stateBody = CombatPackets.BuildChangeState2398(endHandle, CombatPackets.ActorState.PcNormal,
            targetHandle: PrivateShopRegistry.ShopChangeStateWord23, trailing: PrivateShopRegistry.ShopChangeStateWord19);
        await session.SendRmiAsync(ZoneS2COpcodes.OnChangeState, stateBody);
        await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.OnChangeState, w => w.WriteBytes(stateBody));
    }
}
