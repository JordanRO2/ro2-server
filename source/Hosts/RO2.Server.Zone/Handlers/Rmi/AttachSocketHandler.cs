using System;
using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1543 ReqAttachSocket - socket a rune into an opened runehole of an item.</summary>
/// <remarks>
/// <para>
/// <b>Two effects share this opcode</b>, told apart by the ItemReinforceInfo row of the item at @0:
/// <c>Reinforce_Type</c> 0 (runes, ItemInfo Item_Type 29) fills the runehole at @16 with the row's
/// <c>Reinforce_ID</c>, which is the ItemWorkbench runehole model this handler implements;
/// <c>Reinforce_Type</c> 2 (the Item_Type 32 blessings, whetstones, oils and embroideries, e.g. 22290033 /
/// 22290036) leaves the runeholes alone and writes a TIMED enchant into the record: record+17 u16 = the row's
/// Reinforce_ID and record+19 u16 = its <c>Duration</c> in seconds. The seconds drop by 10 per 10 s server tick,
/// and only while the item is WORN by a logged-in player; applying again restarts the full Duration; at 0 the id
/// stays at record+17 with +19 = 0 (a spent enchant); nothing announces the expiry, the next periodic 2498 just
/// drops the bonus. The target's ItemInfo Equip_Slot bit must be set in the row's <c>TargetItem_Parts</c>. The
/// bonus is the row's ItemReinforceTraitInfo lines. Blessings are applied here (<see cref="TimedEnchant"/>): the
/// item keeps the id and the seconds (InventoryItem.EnchantReinforceId / EnchantSeconds),
/// WorldEntryService.WriteItemInstance writes them at record+17/+19 of every record,
/// StatService.ComputeEquipmentStats folds the lines while seconds are left, and TimedEnchantService counts them down
/// on worn items and re-sends the 2498 when one runs out. The answer is retail's: the 2329 on the used item
/// (useSource 1), then the 2786 with the target's record, then the 2498 for a worn target.
/// </para>
/// <para>
/// <b>Seed runes</b> (ItemInfo Item_Type 49, e.g. 22261100 BlueSeed Rune) are Reinforce_Type 0 too, but the
/// hole never shows their own Reinforce_ID: it shows a rune ROLLED from the seed's <c>ItemInfo.RandomSet_ID</c>
/// set (22261100 -> RandomSet 1100 = BlueSeed Rune[10..50], items 22261101..22261141), and the draw is
/// weighted by <c>Ratio</c>. Every seed set resolves, each member being an Item_Type 29 rune with a single
/// ItemReinforceInfo row of the seed's own TargetItem_Socket_Type. The hole keeps the ROLLED rune's template, so
/// ItemWorkbench.ReinforceId writes retail's value, StatService applies that rune's bonus (the seed rows have no
/// ItemReinforceTraitInfo line of their own), and an extraction (1544) hands that rune back (ItemWorkbench.ReinforceId
/// remarks).
/// </para>
/// <para>
/// <b>Hole checks.</b> A hole is only filled if it is opened (ItemWorkbench.OpenedHoles, the
/// CardsJson length that 1542 grows) and its kind accepts the rune: the kind is a SocketGroupType row whose
/// <c>TargetItem_Socket_Type1..5</c> list the ItemReinforceInfo <c>TargetItem_Socket_Type</c> it takes (e.g. kind 6 =
/// {7,5,8,1} takes socket types 5 and 8). An opened hole that already holds a rune is OVERWRITTEN, not refused, and
/// no 2333 or 2787 hands the old rune back. CardsJson is never grown past the holes that 1542 opened.
/// </para>
/// <para>
/// <b>Refusals</b> go out as a 2786 with the code over an all-zero record and no 2329 (e.g. result 3 for a card:
/// ItemInfo Item_Type 13, no ItemReinforceInfo row). Each code means what the client says it means: on a non-zero
/// result the client shows a message chosen by the code: 1 = "Unable to find Rune
/// item", 2 = "Unable to find Target item", 3 = "It is not a Rune item", 4 = "Wrong socket slot index", 5 = "This
/// Rune is not correct for the Socket type", 6 = "Unable to equip to Target item (location or level etc)".
/// </para>
/// <para>
/// <b>The 2498 goes out only for a worn target.</b> The 2786 of a successful fill on a worn target (bag 0) is
/// followed by a 2498; for a target in a bag nothing is re-sent, since a rune in a bag item changes no stat.
/// </para>
/// </remarks>
public class AttachSocketHandler : ZoneRmiHandlerBase<AttachSocketHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqAttachSocket;
    public override string Name => "AttachSocket";

 /// <summary>ItemReinforceInfo.Reinforce_Type of the runes that go into a runehole.</summary>
    private const byte RuneholeReinforceType = 0;

 /// <summary>ItemInfo.Item_Type of the seed runes, which fill the hole with a rune rolled from their RandomSet
 /// (see the class remarks).</summary>
    private const byte SeedRuneItemType = 49;

 /// <summary>2786 result 1, "Unable to find Rune item": no stack of the rune in the bag.</summary>
    private const ushort RuneNotFound = 1;

 /// <summary>2786 result 2, "Unable to find Target item".</summary>
    private const ushort TargetNotFound = 2;

 /// <summary>2786 result 3, "It is not a Rune item": no ItemReinforceInfo row. Retail's code
 /// for a card.</summary>
    private const ushort NotARune = 3;

 /// <summary>2786 result 4, "Wrong socket slot index": the hole is not open.</summary>
    private const ushort WrongSocketIndex = 4;

 /// <summary>2786 result 5, "This Rune is not correct for the Socket type".</summary>
    private const ushort WrongSocketType = 5;

 /// <summary>
 /// 2786 result 6, "Unable to equip to Target item (location or level etc)": a timed enchant on a
 /// target of the wrong part or level (<see cref="TimedEnchant.Fits"/>; the client's text), and a seed rune whose
 /// RandomSet has no rune to roll, which the shipped data
 /// never has (no code says "unsupported", and this is the one that blames neither the rune's identity nor the hole).
 /// </summary>
    private const ushort CannotApply = 6;

    /// <summary>1543 body: <c>[u64 runeItemUid @0][u64 targetItemUid @8][u8 socketIndex @16]</c>, 17 bytes.</summary>
    private const int RequestLength = 17;
    private const int RuneUidOffset = 0;
    private const int TargetUidOffset = 8;
    private const int SocketIndexOffset = 16;

    /// <summary>InventoryItem.StorageType 0: the item sits in a bag.</summary>
    private const byte StorageBag = 0;

    /// <summary>InventoryItem.StorageType 1: the item is worn (equipped).</summary>
    private const byte StorageWorn = 1;

    /// <summary>2786 body: <c>[u16 result][item record]</c>; the result occupies the first 2 bytes.</summary>
    private const int ResultLength = sizeof(ushort);

    public AttachSocketHandler(IServiceScopeFactory s, ILogger<AttachSocketHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        // Body: [u64 runeItemUid @0][u64 targetItemUid @8][u8 socketIndex @16] = 17 bytes. @0 is the rune (or
        // enchant item) being used, @8 the item being modified: the 2786 answer embeds the updated record of the
        // target. socketIndex is the 0-based hole (0 in blessing requests, where it is ignored).
        if (payload.Length < RequestLength) return;
        var span = payload.Span;
        ulong runeUid = BinaryPrimitives.ReadUInt64LittleEndian(span[RuneUidOffset..]);
        ulong targetUid = BinaryPrimitives.ReadUInt64LittleEndian(span[TargetUidOffset..]);
        byte socketIndex = span[SocketIndexOffset];

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var store = services.GetRequiredService<GameDataStore>();
        var target = await inventory.GetByIdAsync(targetUid);
        var rune = runeUid != targetUid ? await inventory.GetByIdAsync(runeUid) : null;

        ushort code = Check(store, player.CharacterId, rune, target, socketIndex, out var why);
        if (code != Attached)
        {
            Logger.LogInformation("{Name} attach-socket {Rune} into #{Hole} of {Target} refused with {Code}: {Why}",
                player.Name, runeUid, socketIndex + 1, targetUid, code, why);
            await session.SendRmiAsync(ZoneS2COpcodes.AnsAttachSocket, BuildRefusal2786(code), ct);
            return;
        }
        if (TimedEnchant.RowOf(store, rune!.ItemId) is { Reinforce_Type: TimedEnchant.TimedReinforceType } timed)
        {
            // A blessing, whetstone, oil or embroidery: a timed enchant on the item, its runeholes untouched
            // (TimedEnchant remarks). It replaces the item's current enchant and restarts at the row's Duration.
            ushort oldEnchant = target!.EnchantReinforceId;
            TimedEnchant.Apply(target, timed);
            await inventory.UpdateItemAsync(target);
            int usedLeft = await ConsumeOneAsync(session, player, inventory, rune);
            Logger.LogInformation("{Name} put timed enchant {Enchant} ({Seconds}s, from item {Used}) on item uid {Target} (replacing {Old}), {Left} left",
                player.Name, timed.Reinforce_ID, timed.Duration, rune.ItemId, targetUid, oldEnchant, usedLeft);
            await AnswerAsync(session, player, services, inventory, store, target);
            return;
        }

        var sockets = StatService.ParseSockets(target!.CardsJson);

        // What goes into the hole: the rune itself, or for a seed rune the rune rolled from its RandomSet by
        // Ratio (class remarks; Check has already refused a seed whose set has nothing to roll).
        uint socketed = rune.ItemId;
        if (store.GetItem(rune.ItemId) is { Item_Type: SeedRuneItemType })
        {
            var members = SeedRuneMembers(store, rune.ItemId);
            long totalRatio = 0;
            foreach (var m in members) totalRatio += m.Ratio;
            socketed = PickByRatio(members, Random.Shared.NextInt64(totalRatio));
        }

        // Fill the hole with that TEMPLATE id (CardsJson = uint[] per opened hole, 0 = empty); a rune already
        // there is replaced and lost (class remarks). The rune's stat bonus then folds into the sheet via
        // StatService.ComputeEquipmentStats reading this socket content, and ItemWorkbench.ApplyRuneholes shows it
        // as the row's Reinforce_ID.
        uint replaced = sockets[socketIndex];
        sockets[socketIndex] = socketed;
        target.CardsJson = StatService.SerializeSockets(sockets);
        await inventory.UpdateItemAsync(target);

        int left = await ConsumeOneAsync(session, player, inventory, rune);

        Logger.LogInformation("{Name} socketed rune {Rune} (from item {Used}) into item uid {Target} hole #{Hole} (replacing {Old}), {Left} left",
            player.Name, socketed, rune.ItemId, targetUid, socketIndex + 1, replaced, left);

        await AnswerAsync(session, player, services, inventory, store, target);
    }

 /// <summary>
 /// Consume one of the used item (rune or enchant): 2329 OnUseItem on its uid, result 0, useSource 1, remaining =
 /// the stack after, BEFORE the 2786 - retail does so in every successful answer, and sends none for a refusal.
 /// Returns the count left.
 /// </summary>
    private static async Task<int> ConsumeOneAsync(ProudSession session, PlayerState player, IInventoryRepository inventory,
        InventoryItem used)
    {
        int left = used.Count - 1;
        var (bag, slot) = ItemWorkbench.ClientBagSlot(used);
        if (left <= 0) { await inventory.RemoveItemAsync(used.Id); left = 0; }
        else { used.Count = left; await inventory.UpdateItemAsync(used); }
        await session.SendRmiAsync(ZoneS2COpcodes.OnUseItem, writer => writer.WriteBytes(
            CombatPackets.BuildUseItem2329(player.EntityId, slot, bag, used.Id, (byte)Math.Clamp(left, 0, byte.MaxValue), useSource: CombatPackets.UseSourceItemUse)));
        return left;
    }

 /// <summary>The 2786 with the target's updated record, then the 2498 when the target is worn.</summary>
    private static async Task AnswerAsync(ProudSession session, PlayerState player, IServiceProvider services,
        IInventoryRepository inventory, GameDataStore store, InventoryItem target)
    {
        // AnsAttachSocket (2786): fixed 128-byte body, [u16 result][126-byte item record of the socketed item].
        // A refusal (result != 0) carries an all-zero record (BuildRefusal2786). The record carries the runehole
        // block (record+33..67), which the client copies verbatim over its item, so a zeroed block would wipe the
        // holes it shows, the one just filled included. It also carries the timed enchant at record+17/+19
        // (WorldEntryService.WriteItemInstance), which is how the client learns a blessing took.
        var record = ItemWorkbench.RecordWithRuneholes(target, store);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsAttachSocket, writer =>
        {
            writer.WriteUInt16(Attached);  // body+0 result
            writer.WriteBytes(record);     // body+2 updated record
        });

        // Re-push the 2498 sheet so the socketed rune's or the enchant's bonus shows at once, recomputed from the
        // worn gear that now carries it. Only for a WORN target: the 2498 follows the 2786 only for worn targets,
        // never for a target in a bag (class remarks).
        if (target.StorageType == StorageWorn && player.Character is { } ch)
        {
            var equipped = await inventory.GetEquipmentAsync(ch.Id);
            // A socketed rune can add STR/INT (damage), VIT (MaxHP/DEF) or WIS (MaxSP): the sheet (with the active
            // effects folded in), the runtime max and the combat cache are re-resolved together
            // (EffectService.RefreshPcTraitSheet).
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPcTrait,
                services.GetRequiredService<EffectService>().RefreshPcTraitSheet(player, ch, equipped));
            // Cache the weapon's damage range for the combat formula.
            (player.WeaponMinDamage, player.WeaponMaxDamage, player.AutoAttackRange) = StatService.WeaponDamageRange(store, equipped);
        }
    }

 /// <summary>2786 result of a fill: body+0 is 0 and body+2 the target's updated record.</summary>
    internal const ushort Attached = 0;

 /// <summary>
 /// Why a 1543 cannot fill hole <paramref name="socketIndex"/> of <paramref name="target"/> with
 /// <paramref name="rune"/>, as the 2786 result code (the client's texts are on the constants), or
 /// <see cref="Attached"/> when it can. Checked in this order: the rune stack, the rune kind; for a timed enchant
 /// (Reinforce_Type 2) the target and whether it fits (<see cref="TimedEnchant.Fits"/>), the socket index being
 /// ignored (0 in retail's blessing requests); for a rune a seed with nothing to roll, the target, the hole being
 /// open, the hole kind taking the rune's socket type.
 /// </summary>
    internal static ushort Check(GameDataStore store, uint characterId, InventoryItem? rune, InventoryItem? target,
        byte socketIndex, out string why)
    {
        why = "";
        if (rune is null || rune.CharacterId != characterId || rune.StorageType != StorageBag || rune.Count < 1)
        {
            why = "rune is not a stack in the bag";
            return RuneNotFound;
        }
        // The first row per source item, the same one ItemWorkbench.ReinforceId writes as the hole value.
        uint runeItemId = rune.ItemId;
        var reinforce = store.ItemReinforceInfo.FirstOrDefault(r => r.SourceItem_ID == runeItemId);
        if (reinforce is null)
        {
            why = $"item {runeItemId} has no ItemReinforceInfo row";
            return NotARune;
        }
        if (reinforce.Reinforce_Type == TimedEnchant.TimedReinforceType)
        {
            if (target is null || target.CharacterId != characterId || target.StorageType is not (StorageBag or StorageWorn))
            {
                why = "target is not in the bag or worn";
                return TargetNotFound;
            }
            if (store.GetItem(target.ItemId) is not { } targetTemplate || !TimedEnchant.Fits(reinforce, targetTemplate))
            {
                why = $"timed enchant {reinforce.Reinforce_ID} (parts 0x{reinforce.TargetItem_Parts:X8}, level {reinforce.TargetItem_ReqLevel}) does not fit item {target.ItemId}";
                return CannotApply;
            }
            return Attached;
        }
        if (reinforce.Reinforce_Type != RuneholeReinforceType)
        {
            why = $"item {runeItemId} is a Reinforce_Type {reinforce.Reinforce_Type} row, which neither fills a hole nor enchants";
            return CannotApply;
        }
        if (store.GetItem(runeItemId) is { Item_Type: SeedRuneItemType } seed && SeedRuneMembers(store, runeItemId).Count == 0)
        {
            why = $"seed rune {runeItemId} has no rune to roll in RandomSet {seed.RandomSet_ID}";
            return CannotApply;
        }
        if (target is null || target.CharacterId != characterId || target.StorageType is not (StorageBag or StorageWorn))
        {
            why = "target is not in the bag or worn";
            return TargetNotFound;
        }
        if (socketIndex >= ItemWorkbench.OpenedHoles(target))
        {
            why = $"hole #{socketIndex + 1} is not open";
            return WrongSocketIndex;
        }
        byte kind = ItemWorkbench.HoleKind(store, target.ItemId, socketIndex);
        if (!HoleAccepts(store, kind, reinforce.TargetItem_Socket_Type))
        {
            why = $"hole #{socketIndex + 1} (kind {kind}) does not take socket type {reinforce.TargetItem_Socket_Type}";
            return WrongSocketType;
        }
        return Attached;
    }

 /// <summary>
 /// A refused 2786: <c>[u16 code][126 zero bytes]</c>, 128 bytes like every 2786. A non-zero
 /// result comes with an all-zero record, e.g. the refusal for a card.
 /// </summary>
    internal static byte[] BuildRefusal2786(ushort code)
    {
        var body = new byte[ResultLength + ItemPackets.ItemRecordLength];
        BinaryPrimitives.WriteUInt16LittleEndian(body, code);
        return body;
    }

 /// <summary>
 /// The runes a seed rune can roll into, with their draw weight: the RandomSet rows whose <c>ID</c> is the seed's
 /// <c>ItemInfo.RandomSet_ID</c>, kept when their <c>Ratio</c> is non-zero and their item is a runehole rune
 /// (Reinforce_Type 0) of the seed's own TargetItem_Socket_Type, so whatever is rolled fits the hole that
 /// <see cref="Check"/> validated against the seed. All 45 seed sets of the shipped data pass whole. Every row of
 /// the set is used whatever its NationEnable (set 2100 has one row at 161 among twenty at 33): which rows a
 /// region draws from is not established. Empty for an item that is not a seed rune.
 /// </summary>
    internal static List<(uint ItemId, uint Ratio)> SeedRuneMembers(GameDataStore store, uint seedItemId)
    {
        var members = new List<(uint ItemId, uint Ratio)>();
        var seed = store.GetItem(seedItemId);
        var seedRow = store.ItemReinforceInfo.FirstOrDefault(r => r.SourceItem_ID == seedItemId);
        if (seed is null || seed.RandomSet_ID == 0 || seedRow is null) return members;
        uint setId = seed.RandomSet_ID;
        foreach (var row in store.RandomSet)
        {
            if (row.ID != setId || row.Ratio == 0) continue;
            uint item = row.Item;
            var rolled = store.ItemReinforceInfo.FirstOrDefault(r => r.SourceItem_ID == item);
            if (rolled is null || rolled.Reinforce_Type != RuneholeReinforceType
                || rolled.TargetItem_Socket_Type != seedRow.TargetItem_Socket_Type) continue;
            members.Add((item, row.Ratio));
        }
        return members;
    }

 /// <summary>
 /// The member whose Ratio band holds <paramref name="draw"/>, a number in [0, sum of the Ratios): members take
 /// consecutive bands of their Ratio's width in list order, so each is drawn with probability Ratio / total.
 /// 0 when the draw is past the last band.
 /// </summary>
    internal static uint PickByRatio(IReadOnlyList<(uint ItemId, uint Ratio)> members, long draw)
    {
        foreach (var (itemId, ratio) in members)
        {
            if (draw < ratio) return itemId;
            draw -= ratio;
        }
        return 0;
    }

 /// <summary>
 /// Whether a hole of <paramref name="kind"/> (a SocketGroupTypeID) takes a rune of ItemReinforceInfo
 /// <c>TargetItem_Socket_Type</c> <paramref name="socketType"/>: one of the row's TargetItem_Socket_Type1..5.
 /// </summary>
    private static bool HoleAccepts(GameDataStore store, byte kind, byte socketType)
    {
        if (kind == 0 || socketType == 0) return false;
        var row = store.SocketGroupType.FirstOrDefault(g => g.SocketGroupTypeID == kind);
        return row is not null
            && (row.TargetItem_Socket_Type1 == socketType || row.TargetItem_Socket_Type2 == socketType
                || row.TargetItem_Socket_Type3 == socketType || row.TargetItem_Socket_Type4 == socketType
                || row.TargetItem_Socket_Type5 == socketType);
    }
}
