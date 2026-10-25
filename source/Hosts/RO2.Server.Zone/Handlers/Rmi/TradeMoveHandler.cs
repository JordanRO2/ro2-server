using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Economy.Domain;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1353 ReqPCTradeMove - Move item into trade window</summary>
public class TradeMoveHandler : ZoneRmiHandlerBase<TradeMoveHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPCTradeMove;
    public override string Name => "TradeMove";

    /// <summary>msgTag of a 1353 body (0xE2B6), at payload offset +4 of its compact-length envelope.</summary>
    internal const ushort MsgTag = 58038;

    /// <summary>
    /// Payload bytes before the first field of a 1353: the 0x01 marker, the 1-byte compact length, the u16 selfLen,
    /// the u16 msgTag and the u32 self PC handle (1 + 1 + 2 + 2 + 4).
    /// </summary>
    private const int EnvelopeLength = 10;

    /// <summary>Shortest payload that holds the item uid: the envelope plus the u64 uid.</summary>
    private const int MinPayloadWithUid = EnvelopeLength + sizeof(ulong);

    /// <summary>Full payload length of a 1353 (24 B): the envelope, the u64 uid, the u32 template id and the u16 count.</summary>
    private const int PayloadWithQuantityLength = MinPayloadWithUid + sizeof(uint) + sizeof(ushort);

    /// <summary>InventoryItem.StorageType of a bag row; worn, bank and bag-window rows use other values.</summary>
    private const byte BagStorageType = 0;

    /// <summary>2384 result 0: the drag was accepted and the item enters the window.</summary>
    private const short DragAccepted = 0;

    public TradeMoveHandler(IServiceScopeFactory s, ILogger<TradeMoveHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        // C2S body (compact-BA, marker 0x01, 24 B): contents = [u16 selfLen][u16 msgTag 58038]
        // [u32 selfHandle][u64 itemUid][u32 itemId][u16 count]. In PAYLOAD offsets (marker+1 length byte
        // + 2 selfLen + 2 msgTag + 4 handle = EnvelopeLength), the item instance uid is the u64 at body+10 — its
        // high dword is a per-session constant.
        if (payload.Length < MinPayloadWithUid) return;
        var reader = new PacketReader(payload.Span);
        reader.Skip(EnvelopeLength);
        var itemUid = reader.ReadUInt64();

        // body+18 u32 item template id, body+22 u16 quantity. The quantity used to be dropped, so the whole
        // stack was always offered. It is what the window shows: the 2384 answering a 1353 carries the offered
        // quantity at record+14 (stack count).
        int requested = 0;
        if (payload.Length >= PayloadWithQuantityLength)
        {
            reader.Skip(sizeof(uint)); // item template id
            requested = reader.ReadUInt16();
        }

        var trade = TradeSessionService.Instance.Get(player.SessionId);
        if (trade == null || trade.Completing) return;

        // A LOCKED side's drag gets no answer at all: the offer is frozen, and AddItem below refuses
        // it too; returning here also keeps the refusals that follow from answering it.
        if (trade.IsLocked(player.SessionId)) return;

        // Ownership guard: the offered item must still belong to THIS player. uids are global, so without
        // this a client could offer another player's item by uid. StorageType 0 = the bag (worn gear /
        // bank are not tradeable through this window).
        var inventory = services.GetRequiredService<IInventoryRepository>();
        var item = await inventory.GetByIdAsync(itemUid);
        if (item is null || item.CharacterId != player.CharacterId || item.StorageType != BagStorageType)
            return;

        uint handle = trade.HandleFor(player.SessionId);

        // Refused with the "You can't trade the item." answer, 2384 result 20063 to the OFFERER only,
        // carrying a record built from the request (TradePackets.BuildRefusedDragRecord) — see
        // RefusedAsNotTradeable for the two conditions.
        var items = services.GetRequiredService<GameDataStore>().Items;
        if (items.TryGetValue(item.ItemId, out var data) && RefusedAsNotTradeable(data, item))
        {
            Logger.LogInformation("{Name} tried to trade untradeable or bound item {ItemId} (uid {Uid}, bound {Bound})",
                player.Name, item.ItemId, itemUid, item.Bound);
            await session.SendRmiAsync(ZoneS2COpcodes.OnDragTradePC, TradePackets.BuildOnDragTradePC(handle,
                TradePackets.BuildRefusedDragRecord(itemUid, item.ItemId, requested), TradePackets.DragItemNotTradeable));
            return;
        }

        // Clamp to what the stack really holds: the echo is authoritative, so the partner is shown (and
        // agrees to) a quantity that exists. 0 / absent / more-than-owned fall back to the whole stack.
        // A strict partial is legal on the wire; the confirm-time swap splits the stack for it rather than
        // moving more than shown.
        int stack = Math.Max(1, item.Count);
        int offered = requested >= 1 && requested < stack ? requested : stack;
        var shown = offered == stack ? item : TradeOfferQuantities.WithCount(item, offered);
        var record = WorldEntryService.BuildItemRecord126(shown);

        // The item as the window shows it is frozen with the quantity (E-H4): the swap re-reads the row under
        // this uid and moves nothing unless it is still exactly this.
        var change = trade.AddItem(player.SessionId, itemUid, offered, TradeInventoryAdapter.SnapshotOf(item, offered));
        switch (change)
        {
            case TradeOfferChange.Applied:
                Logger.LogInformation("{Name} offers {Qty}x item uid {Uid} to trade", player.Name, offered, itemUid);
                // 2384 OnDragTradePC to BOTH sides, carrying the OFFERING PC's handle + the real 126-byte
                // item record with its stack count set to the offered quantity. The handle disambiguates
                // which trade window (self vs partner) the client places it in. Every result-0
                // 2384 carries the requested count at record+14.
                var body = TradePackets.BuildOnDragTradePC(handle, record, DragAccepted);
                foreach (var sid in new[] { trade.SessionA, trade.SessionB })
                {
                    var s = Broadcast.GetSession(sid);
                    if (s != null)
                        await s.SendRmiAsync(ZoneS2COpcodes.OnDragTradePC, body);
                }
                break;

            case TradeOfferChange.AlreadyOffered:
            case TradeOfferChange.WindowFull:
                // The uid is already on the table, or the side already has TradeOffer.MaxItems: answer
                // 2384 result 1 to the OFFERER only (the partner never gets a second result-0 for the uid), with the
                // item's record at the requested count.
                // The quantity already offered is left as it was, so the partner's window stays right.
                Logger.LogDebug("{Name} re-offered uid {Uid} or has a full window ({Change})",
                    player.Name, itemUid, change);
                await session.SendRmiAsync(ZoneS2COpcodes.OnDragTradePC,
                    TradePackets.BuildOnDragTradePC(handle, record, TradePackets.DragRefused));
                break;

            default:
                // SideLocked (the lock landed between the check above and AddItem) or Closed: no answer.
                break;
        }
    }

    /// <summary>
    /// True when a bag item offered by 1353 gets the 2384 <see cref="TradePackets.DragItemNotTradeable"/> answer
    /// instead of entering the trade window: its template is flagged untradeable (ItemInfo Is_Trade 0), or the
    /// instance is BOUND, i.e. its record+0x1B carries <see cref="WorldEntryService.RecordBoundFlag"/>
    /// (ItemInfo Bind_Type 2, or Bind_Type 1 once worn, <see cref="InventoryItem.Bound"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The 2384 result 20063 answer covers both conditions. Is_Trade 0 is also checked by the client before it
    /// sends the 1353, so that half only guards against a client that skips its own check.
    /// </para>
    /// <para>
    /// The bind half is what is actually refused on: an accepted record never carries the 0x02 bound bit in its
    /// bind flags, while the refused ones name Is_Trade 1 templates (Bind_Type 1 and 2) whose instances are bound.
    /// Bind_Type 1 alone is not refused (such templates trade until worn). The refusal record itself is zeroed
    /// apart from uid, template and count, so it does not show the bit (BuildRefusedDragRecord matches that).
    /// </para>
    /// <para>
    /// Limits: rows stored before InventoryItem.Bound existed read as unbound, so a Bind_Type-1 item worn
    /// before then still trades. A further result (20167) exists on the wire, but its cause is not
    /// identified and it is not reproduced.
    /// </para>
    /// <para>
    /// StorageType is 0 here (the handler returns earlier for anything not in the bag), so the bind test
    /// reduces to Bind_Type 2, or Bind_Type 1 with Bound set — the same test the private-shop listing gate
    /// uses (PrivateShopAddItemHandler.IsBound).
    /// </para>
    /// </remarks>
    internal static bool RefusedAsNotTradeable(ItemData template, InventoryItem item)
        => TradeGate.RefusedAsNotTradeable(template, item);
}
