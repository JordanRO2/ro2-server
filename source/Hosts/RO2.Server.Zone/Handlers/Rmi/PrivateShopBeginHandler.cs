using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1478 ReqBeginPrivateShop: opens the sender's private shop under the given title.</summary>
/// <remarks>
/// <para>
/// Request, raw 61 bytes: +10 the seller's own handle, +14 the shop title as wchar_t[21]; every other byte is
/// uninitialised and ignored.
/// </para>
/// <para>
/// Answered with three messages, in this order: 2398 (OnChangeState, the seller into the shop state), 2651
/// (AnsBeginPrivateShop, to the seller only) and 2663 (NfyBeginPrivateShop, the stall sign, to the seller and to
/// the players around it). 2651 and 2663 carry the same title and category byte:
/// </para>
/// <list type="bullet">
/// <item>2398: <c>[handle@4][handle@8][u8 0x1F@12][0@13][0@14][u32 0@15][u32 2@19][u32 7@23]</c>. State 0x1F is
/// the open shop; the state-0 2398 sent at 1480 undoes it, with the same +19 and +23 words.</item>
/// <item>2651, raw 61: zero at +0..13 (the u16 result at +8 is 0), the title at +14, category u8 @56 and a u32 @57
/// holding the seller's artisan title (see below).</item>
/// <item>2663, compact 63: <c>[u16 innerSize 61][u16 msgTag 58417][u32 handle@4][u16 0@8][u32 handle@10]
/// [title@14][u8 category@56][u32 flags@57]</c>. The client gives the title to the seller's actor and tests
/// the category against the four sign bits {8, 1, 2, 4}.</item>
/// </list>
/// <para>
/// The category is a bit mask of what the shop offers: 8 items (a listed item is still in the bag), 1 crafts (a
/// craft offer is recorded and not withdrawn), 2 refine service (the seller's last 1670 carried a non-zero
/// value, the same value the buyers' 3000 carries) and 4 random refine (the seller has a commission, the same
/// value the buyers' 3006 carries; it ends with the shop at 1480).
/// </para>
/// <para>
/// The u32 at @57 (2651) / blob+57 (2663) is the seller's weekly artisan title, sent as 0 because this server has
/// no artisan ranking. As flags it holds at most one bit: 0x1/0x10/0x100 mark the top craft/refine/hone artisan
/// by cumulative score and 0x2/0x20/0x200 the top by growth rate; the client raises the matching sign icon only
/// when its category bit is also set. 0 is the value of a seller with no title.
/// </para>
/// </remarks>
public class PrivateShopBeginHandler : ZoneRmiHandlerBase<PrivateShopBeginHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqBeginPrivateShop;
    public override string Name => "PrivateShopBegin";
    public PrivateShopBeginHandler(IServiceScopeFactory s, ILogger<PrivateShopBeginHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    /// <summary>Length of the 1478 request: the title (offset 14, 42 bytes) is followed by 5 more bytes.</summary>
    private const int RequestLength = 61;

    /// <summary>msgTag of the 2663 NfyBeginPrivateShop notification (58417).</summary>
    private const ushort MsgTagNfyBeginPrivateShop = 0xE431;

    /// <summary>Offset of the shop title in the 1478 request and the 2651 answer (and blob+14 of 2663).</summary>
    private const int NameOffset = 14;

    /// <summary>Artisan-title word of 2651 (+57) and 2663 (blob+57): 0 = no title, there is no artisan ranking here.</summary>
    private const uint NoArtisanTitle = 0;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < RequestLength) return;

        var name = PrivateShopRegistry.NameFromClient(payload.Span.Slice(NameOffset, PrivateShopRegistry.NameBytes));
        PrivateShopRegistry.SetName(player, name);

        // Category from what a buyer can really get now: listed items that are still in the bag, the craft
        // offers (what the buyers' 2658 will list), the refine service of the last 1670 (the value the
        // buyers' 3000 will carry) and a random-refine commission (the value the buyers' 3006 will carry);
        // 0 means none for the last two.
        byte category = 0;
        if (PrivateShopRegistry.TryGet(player, out _, out var listings) && listings.Count > 0)
        {
            var inv = services.GetRequiredService<IInventoryRepository>();
            var bag = await inv.GetByCharacterAsync(player.CharacterId);
            if (PrivateShopRegistry.Live(listings, bag).Count > 0) category |= PrivateShopRegistry.CategoryItems;
        }
        if (PrivateShopRegistry.GetCrafts(player).Count > 0)
            category |= PrivateShopRegistry.CategoryCrafts;
        if (BeginRefineShopHandler.TryGetRefineService(player, out var refineService) && refineService != 0)
            category |= PrivateShopRegistry.CategoryRefineService;
        if (RandomRefineCommissionHandler.TryGetCommission(player, out var commission) && commission != 0)
            category |= PrivateShopRegistry.CategoryRandomRefine;

        Logger.LogInformation("{Name} begins private shop '{Title}' (category {Category})",
            player.Name, System.Text.Encoding.Unicode.GetString(name).TrimEnd('\0'), category);

        var handle = player.EntityId;

        // 2398 OnChangeState, the seller into the shop state, first: to the seller before its 2651 and to the
        // players around it before their 2663. blob+23 goes through the builder's targetHandle slot because that
        // is where it sits; 7 is a constant, not a handle. 1480 sends the state-0 2398 that undoes it.
        var stateBody = CombatPackets.BuildChangeState2398(handle, PrivateShopRegistry.ShopOpenState,
            targetHandle: PrivateShopRegistry.ShopChangeStateWord23, trailing: PrivateShopRegistry.ShopChangeStateWord19);
        await session.SendRmiAsync(ZoneS2COpcodes.OnChangeState, stateBody);
        await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.OnChangeState, w => w.WriteBytes(stateBody));

        // 2651 AnsBeginPrivateShop: the seller's confirmation, raw 61 bytes.
        await session.SendRmiAsync(ZoneS2COpcodes.AnsBeginPrivateShop, writer =>
        {
            writer.WriteZeros(8);                // +0..7
            writer.WriteUInt16(0);               // +8  result (0 = ok)
            writer.WriteZeros(4);                // +10..13
            writer.WriteBytes(name);             // +14 title, wchar_t[21]
            writer.WriteByte(category);          // +56 category
            writer.WriteUInt32(NoArtisanTitle);  // +57 artisan title
        });

        // 2663 NfyBeginPrivateShop: the stall sign, to the seller and to the players around it.
        using var pw = new PacketWriter(64);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagNfyBeginPrivateShop);
            p.WriteUInt32(handle);               // blob+4  seller handle, equal to blob+10
            p.WriteUInt16(0);                    // blob+8  unused, always 0
            p.WriteUInt32(handle);               // blob+10 seller handle, the one the client looks up
            p.WriteBytes(name);                  // blob+14 title
            p.WriteByte(category);               // blob+56 category
            p.WriteUInt32(NoArtisanTitle);       // blob+57 artisan title, same as the 2651's +57
        });
        var nfy = pw.ToArray();
        await session.SendRmiAsync(ZoneS2COpcodes.NfyBeginPrivateShop, nfy);
        await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.NfyBeginPrivateShop, w => w.WriteBytes(nfy));
    }
}
