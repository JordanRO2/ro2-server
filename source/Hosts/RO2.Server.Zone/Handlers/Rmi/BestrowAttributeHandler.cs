using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1662 ReqBestrowAttribute (Fixed 16B) - Bestow attribute to item</summary>
/// <remarks>
/// <para>
/// REQUEST, 16 bytes:
/// <c>[u64 stoneUid @0][u64 targetUid @8]</c>, two item instance uids. The stone is the item in
/// use and the target is the clicked item.
/// </para>
/// <para>
/// ANSWER 2983 AnsBestrowAttribute, 11 bytes: <c>[u16 result][u64 targetUid][u8 element]</c>.
/// On success the u64 is the request's u64 @8 and the element is 0-5. On refusal it is
/// <c>[u16 code][u64 0][u8 0]</c>. On 0 the client writes the element
/// into the bag item, the equipment-window item and the player's equipment entry and shows
/// "@2 element has granted in @1."; a non-zero code is shown as a result-code message (24353, 24354 and 24373
/// have their own texts, anything else, 23006 included, a generic one).
/// </para>
/// <para>
/// BEHAVIOUR (the original server's, repeated here):
/// <list type="bullet">
/// <item>The stone decides the element: each stone yields a single element over all its successful uses. The
/// stones are the Item_Type 65 "attribute bestowal stone" items, and data/EnchantElement.json maps each one to
/// its element (0 = the neutral stone).</item>
/// <item>The stone is consumed: every success is preceded by a 2329 OnUseItem in the same millisecond (its uid
/// is the stone's and useSource is 1), and no refusal is.</item>
/// <item>The element is the item's own state and lasts: retail writes it into the item record at record+68 (it
/// survives relog and shows in 2310, 2327, 2389, 2307 and 2786 records; the element is one byte). So it is stored
/// on the item (<c>InventoryItem.Element</c>) and the record writer puts it back at +68.</item>
/// <item>A worn target is re-dressed: a success on a worn item (bag byte +15 == 0) is followed in the same
/// millisecond by a self 2315 OnPCViewData, and a success on a bag item is not. That 2315's worn record of the
/// item carries the new element at VEC28 +9 (see EquipViewNotifyPackets.WriteEquipRecord28).</item>
/// <item>A non-zero stone needs a neutral item: refusal 24373. A non-zero element can go back to 0 with the
/// neutral stone.</item>
/// <item>A neutral stone needs an elemented item: refusal 24353 ("It cannot grant the element
/// since its elements are same.").</item>
/// <item>No stone: refusal 23006. It carries stone uid 0, which the builder sends when the cursor holds no stone
///. A stone uid this character does not hold gets the
/// same code, and the client only sends a stone it holds.</item>
/// </list>
/// Retail consumes nothing on any refusal, and neither does this handler. The 2983 is sent in the millisecond of
/// its 2329, and the 2315 after it.
/// </para>
/// <para>
/// GAPS, stated rather than papered over:
/// <list type="bullet">
/// <item>The element has no effect on combat here: StatService has no elemental attack or resistance,
/// so it is stored and shown, not applied.</item>
/// <item>A missing or foreign TARGET, and a stone with no EnchantElement row, are dropped without an answer:
/// retail's refusals are the three codes above and nothing else, so what it answers
/// there is not known. 24354 ("not a weapon") is never sent.</item>
/// <item>Other players are not told of the new element (no 2848 broadcast): whether retail sends observers
/// one after a bestow is not known. They see it at the next 2504/2848, which now carry it.</item>
/// </list>
/// </para>
/// </remarks>
public class BestrowAttributeHandler : ZoneRmiHandlerBase<BestrowAttributeHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqBestrowAttribute;
    public override string Name => "BestrowAttribute";

 /// <summary>Retail's refusal for a non-zero element on an item that already has one (see remarks).</summary>
    internal const ushort ResultAlreadyHasElement = 24373;

 /// <summary>Retail's refusal for the neutral stone on an item that is already neutral (see remarks).</summary>
    internal const ushort ResultSameElement = 24353;

 /// <summary>Retail's refusal for a request with no stone, stone uid 0 (see remarks).</summary>
    internal const ushort ResultNoStone = 23006;

 /// <summary>2329 useSource of the consumed stone: 1.</summary>
    private const byte UseSourceStone = 1;

 /// <summary>1662 request length: two u64 item uids (stone @0, target @8).</summary>
    private const int RequestLength = 16;

 /// <summary>2983 answer length: u16 result + u64 target uid + u8 element.</summary>
    private const int AnswerLength = 11;

 /// <summary>2983 result code of a successful bestow.</summary>
    private const ushort ResultSuccess = 0;

 /// <summary>Item <c>StorageType</c> of a worn (equipped) item; 0 is the bag.</summary>
    private const byte StorageWorn = 1;

 /// <summary>Element byte of the neutral stone and of an item with no element.</summary>
    private const byte ElementNeutral = 0;

    public BestrowAttributeHandler(IServiceScopeFactory s, ILogger<BestrowAttributeHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < RequestLength)
        {
            Logger.LogWarning("{Name}: short 1662 body ({Len} B, retail is {Expected})", player.Name, payload.Length, RequestLength);
            return;
        }
        var reader = new PacketReader(payload.Span);
        var stoneUid = reader.ReadUInt64();   // +0 the attribute stone (consumed)
        var targetUid = reader.ReadUInt64();  // +8 the item that receives the element (echoed in 2983)

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var store = services.GetRequiredService<GameDataStore>();

        // No stone (uid 0, or one this character does not hold): refusal 23006, nothing consumed.
        var stone = stoneUid != 0 ? await inventory.GetByIdAsync(stoneUid) : null;
        if (stone is null || stone.CharacterId != player.CharacterId)
        {
            Logger.LogInformation("{Name}: 1662 refused with {Code}, no stone held (stone uid {Stone}, target {Target})",
                player.Name, ResultNoStone, stoneUid, targetUid);
            await session.SendRmiAsync(ZoneS2COpcodes.AnsBestrowAttribute, BuildRefusal2983(ResultNoStone), ct);
            return;
        }

        var target = targetUid != 0 ? await inventory.GetByIdAsync(targetUid) : null;
        if (target is null || target.Id == stone.Id || target.CharacterId != player.CharacterId)
        {
            Logger.LogWarning("{Name}: 1662 names a target it does not hold (stone {Stone}, target {Target})",
                player.Name, stoneUid, targetUid);
            return;
        }

        var row = store.EnchantElement.FirstOrDefault(e => e.Item_ID == stone.ItemId);
        if (row is null)
        {
            Logger.LogWarning("{Name}: 1662 stone {Stone} (template {Tpl}) has no EnchantElement row",
                player.Name, stone.Id, stone.ItemId);
            return;
        }

        // The two element refusals (see remarks), before anything is consumed.
        if (RefusalFor(row.Element, target.Element) is ushort code)
        {
            Logger.LogInformation("{Name}: 1662 refused with {Code}, item {Target} has element {Old} (stone element {New})",
                player.Name, code, target.Id, target.Element, row.Element);
            await session.SendRmiAsync(ZoneS2COpcodes.AnsBestrowAttribute, BuildRefusal2983(code), ct);
            return;
        }

        // Store the element on the item before the stone goes, so a failed write never costs a stone.
        // The item record writer puts it back at record+68, and the worn-item record at +9.
        target.Element = row.Element;
        await inventory.UpdateItemAsync(target);

        // Consume one stone and push the bag update first: 2329 precedes 2983.
        var (bag, slot) = ItemWorkbench.ClientBagSlot(stone);
        int remaining = stone.Count - 1;
        if (remaining <= 0) { await inventory.RemoveItemAsync(stone.Id); remaining = 0; }
        else { stone.Count = remaining; await inventory.UpdateItemAsync(stone); }
        await session.SendRmiAsync(ZoneS2COpcodes.OnUseItem,
            w => w.WriteBytes(CombatPackets.BuildUseItem2329(player.EntityId, slot, bag, stone.Id,
                (byte)Math.Clamp(remaining, 0, 255), useSource: UseSourceStone)), ct);

        await session.SendRmiAsync(ZoneS2COpcodes.AnsBestrowAttribute, BuildSuccess2983(target.Id, row.Element), ct);

        // A worn target: send a self 2315 OnPCViewData with the worn records rebuilt from the saved rows,
        // the same builder the world entry uses. A bag target gets none.
        if (target.StorageType == StorageWorn)
        {
            var ch = player.Character
                ?? await services.GetRequiredService<ICharacterRepository>().GetByIdAsync(player.CharacterId);
            if (ch is not null)
            {
                var worn = await inventory.GetEquipmentAsync(player.CharacterId);
                var viewData = ActorStatePackets.BuildOnPCViewData2315(player, ch, worn, services.GetRequiredService<IGuildEmblemRepository>());
                await session.SendRmiAsync(ZoneS2COpcodes.OnPCViewData, w => w.WriteBytes(viewData), ct);
            }
            else
            {
                Logger.LogWarning("{Name}: no character row, the 2315 after the bestow on worn item {Target} is not sent",
                    player.Name, target.Id);
            }
        }

        Logger.LogInformation("{Name}: bestowed element {Element} on item {Target} with stone {Stone} ({Left} left, worn={Worn})",
            player.Name, row.Element, target.Id, stone.ItemId, remaining, target.StorageType == StorageWorn);
    }

 /// <summary>
 /// The refusal a stone of element <paramref name="stoneElement"/> gets on an item of element
 /// <paramref name="targetElement"/>, or null when the bestow goes ahead: a non-zero element only onto a neutral
 /// item (24373), the neutral stone only onto an elemented one (24353). See the class remarks.
 /// </summary>
    internal static ushort? RefusalFor(byte stoneElement, byte targetElement)
    {
        if (stoneElement != ElementNeutral && targetElement != ElementNeutral) return ResultAlreadyHasElement;
        if (stoneElement == ElementNeutral && targetElement == ElementNeutral) return ResultSameElement;
        return null;
    }

 /// <summary>2983 refusal, retail's 11 bytes <c>[u16 code][u64 0][u8 0]</c>.</summary>
    internal static byte[] BuildRefusal2983(ushort code)
    {
        using var w = new PacketWriter(AnswerLength);
        w.WriteUInt16(code); // +0  result
        w.WriteUInt64(0);    // +2  target uid: 0 on a refusal
        w.WriteByte(ElementNeutral); // +10 element: 0 on a refusal
        return w.ToArray();
    }

 /// <summary>2983 success, <c>[u16 0][u64 targetUid][u8 element]</c>.</summary>
    internal static byte[] BuildSuccess2983(ulong targetUid, byte element)
    {
        using var w = new PacketWriter(AnswerLength);
        w.WriteUInt16(ResultSuccess); // +0  result
        w.WriteUInt64(targetUid);     // +2  the target's uid (the request's u64 @8)
        w.WriteByte(element);         // +10 element from EnchantElement (0 = neutral stone)
        return w.ToArray();
    }
}
