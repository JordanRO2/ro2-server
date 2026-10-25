using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1326 ReqItemDetailInfo: the client asks for one item's full record. The answer is RMI 2326 OnItemInfo,
/// carrying a 126-byte item record.
/// </summary>
/// <remarks>
/// <para>
/// The reply's compact length uses marker 0x02 for a two-byte length. The client cannot decode a one-byte marker
/// that carries a two-byte length.
/// </para>
/// <para>
/// Request body: <c>[u8 marker=0x01][u8 16][u16 selfLen=16][u16 msgTag=0xE2CF][u32 actorHandle][u64 itemUid]</c>.
/// The item uid is the u64 at body+10.
/// </para>
/// <para>
/// The reply goes unicast to the requester. Items unknown to the character get no reply.
/// </para>
/// </remarks>
public class ItemInfoHandler : ZoneRmiHandlerBase<ItemInfoHandler>
{
    /// <summary>C2S 1326 ReqItemDetailInfo.</summary>
    public override ushort Opcode => ZoneC2SOpcodes.ReqItemDetailInfo;

    public override string Name => "ItemInfo";

    /// <summary>Body offset of the u64 item uid in C2S 1326.</summary>
    private const int RequestItemUidOffset = 10;

    /// <summary>Body length of C2S 1326 (18 bytes).</summary>
    private const int RequestBodyLen = 18;

    public ItemInfoHandler(
        IServiceScopeFactory scopeFactory,
        ILogger<ItemInfoHandler> logger,
        PlayerStateManager playerManager,
        BroadcastService broadcast)
        : base(scopeFactory, logger, playerManager, broadcast)
    {
    }

    protected override async Task HandleAsync(
        ProudSession session,
        ReadOnlyMemory<byte> payload,
        PlayerState player,
        IServiceProvider services,
        CancellationToken ct)
    {
        if (payload.Length < RequestBodyLen)
            return;

        var reader = new PacketReader(payload.Span);
        reader.Skip(RequestItemUidOffset);
        var itemUid = reader.ReadUInt64();

        // The wire field is the item instance uid (InventoryItem.Id), not the item template id.
        InventoryItem? item = null;
        try
        {
            var inventory = services.GetRequiredService<IInventoryRepository>();
            item = await inventory.GetByIdAsync(itemUid);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Session {Id}: inventory lookup failed for item uid {Uid}",
                session.SessionId, itemUid);
        }

        if (item is null || item.CharacterId != player.CharacterId)
        {
            // No answer for an unknown item or one owned by another character; a fabricated record would be worse
            // than silence.
            Logger.LogDebug("Session {Id}: ItemInfo request for unknown item uid {Uid}",
                session.SessionId, itemUid);
            return;
        }

        // The same 126-byte record writer the inventory list in RMI 2310 uses for the same item.
        var record = WorldEntryService.BuildItemRecord126(item);
        await session.SendRmiAsync(ZoneS2COpcodes.OnItemInfo,
            writer => writer.WriteBytes(ItemPackets.BuildItemInfo2326(record)));

        Logger.LogDebug("Session {Id}: ItemInfo for uid {Uid} (template {Tid})",
            session.SessionId, itemUid, item.ItemId);
    }
}
