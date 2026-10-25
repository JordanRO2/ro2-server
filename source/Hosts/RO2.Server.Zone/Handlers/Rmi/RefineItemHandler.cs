using System;
using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Configuration;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1668 ReqRefineItem — refine (enhance) an equip by one grade, consuming a catalyst + zeny.
/// </summary>
/// <remarks>
/// <para>
/// WIRE. The RMI body is a compact-BA blob:
/// <c>[u8 marker][blob]</c> where the 50-byte blob is
/// <c>+0 u16 selfLen=50, +2 u16 msgTag=58507, +4 u32 typeId, +8 u8 mode, +9/+13 player handles,
/// EQUIP{+17 container,+18 slot,+19 flag,+20 u32 uidLo,+24 u32 uidHi},
/// MATERIAL{+28 container,+29 slot,+30 flag,+31 u32 uidLo,+35 u32 uidHi}, THIRD{+39..+49}</c>.
/// The two items are identified by their u64 instance UID (KharaID = our InventoryItem.Id), not a
/// template id.
/// </para>
/// <para>
/// OUTCOME is server-authoritative (the client has no roll logic; it only displays a result and mirrors
/// the new level). The roll reads the real tables as named, all rates /10000 (see <see cref="RollRefine"/>):
/// <list type="bullet">
/// <item><c>REFINE_RATE</c> keyed INDEX = target level (refining +N uses INDEX=N+1): Need_Gold, Succ_Rate,
///   Break_Rate = survival on failure, Down_Rate = chance a surviving failure still drops, Down_Type 1 = drop to
///   the band floor (+10 from +11 or more, else +0). INDEX 1 and 11 are guaranteed (Succ 10000), which is what makes
///   +0 and +10 the band floors. MaxRefine is the table's largest INDEX. Nothing is ever destroyed: where the original rules
///   would destroy (survival fails), this server resets the item to its band floor.</item>
/// <item>catalyst <c>REFINE_ITEM</c> keyed ITEM_ID: Succ_Rate ADDS to success (Blessing Powder +50%);
///   <c>2_/3_GRADE_RATE</c> give the LUCKY multi-jump (result is +2/+3 in one attempt - Lucky Powder = 50%
///   +2, 10% +3); Break_Rate ADDS to survival (Infinium 10000 = never reset; ITEM_TYPE 1, it rides the THIRD
///   slot).</item>
/// </list>
/// The outcome-to-chat mapping is client-side (2997 blob+10 u32): 0 = success (chat 7928 "L→L+n"),
/// 1 = fail-kept (chat 11352, level unchanged), 2 = break/destroy (chat 7932, client clears the slot at
/// blob+30/+31; never sent, nothing is destroyed), 3 = drop (chat 7930; also sent for a drop to the band
/// floor, the 2997 record carries the new level). The refine stat bonus is computed by StatService from the
/// persisted Refine byte and delivered via the 2498/2848 re-push. A 2997 must be sent or the refine window
/// stays locked. Items are never removed, so 2379 OnDestroyItem is never sent; each consumed catalyst is
/// removed from the bag via 2329 OnUseItem.
/// </para>
/// <para>
/// REFINE SHOP. A refine can be performed in another player's refine shop: blob+8 = 1, blob+4 == blob+13 ==
/// the sender, blob+9 = another PC (tag 0x11), the shop owner, and the request follows a 1481 of the same
/// session for that owner whose 3000 carried the refine-service flag 1. The owner is told with 3005
/// OnRefineItemToSeller (RefineToSellerPackets). The pairing of a 1668 to its 3005 is read from the handles.
/// It is sent when blob+9 names a player whose shop this buyer has open (PrivateShopRegistry.IsViewer) and
/// whose last 1670 offered the service (BeginRefineShopHandler.TryGetRefineService), and only for a refine
/// that happened. The owner may also earn a commission; its rates are not in any request of the owner
/// (BeginRefineShopHandler remarks) and this server charges the buyer none, so no money is moved or
/// announced for it.
/// </para>
/// </remarks>
public class RefineItemHandler : ZoneRmiHandlerBase<RefineItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqRefineItem;
    public override string Name => "RefineItem";
    public RefineItemHandler(IServiceScopeFactory s, ILogger<RefineItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    // ---- 1668 request blob (offsets from the start of the inner blob; see the class remarks) ----

    /// <summary>Request blob +9: u32 handle of the refine shop's owner (0 or the sender when not a shop refine).</summary>
    private const int OffShopOwnerHandle = 9;

    /// <summary>Request blob +20: u64 instance UID of the equip being refined (EQUIP slot, +20 lo / +24 hi).</summary>
    private const int OffEquipUid = 20;

    /// <summary>Request blob +31: u64 instance UID of the catalyst (MATERIAL slot, +31 lo / +35 hi).</summary>
    private const int OffMaterialUid = 31;

    /// <summary>Request blob +42: u64 instance UID of the optional second powder (THIRD slot, +42 lo / +46 hi).</summary>
    private const int OffThirdUid = 42;

    /// <summary>Minimum blob length: through the MATERIAL UID.</summary>
    private const int MaterialUidEnd = OffMaterialUid + sizeof(ulong);

    /// <summary>Blob length at which the THIRD slot UID is present (the full 50-byte blob).</summary>
    private const int ThirdUidEnd = OffThirdUid + sizeof(ulong);

    // ---- Client container ids (bag window addressing) ----

    /// <summary>Container id of the worn-equipment window.</summary>
    private const byte EquipmentContainer = 0;

    /// <summary>Container id of the main bag.</summary>
    private const byte MainBagContainer = 1;

    /// <summary>First container id of the extra bags (containers 2 to 4).</summary>
    private const byte FirstExtraBagContainer = 2;

    /// <summary>Last container id of the extra bags.</summary>
    private const byte LastExtraBagContainer = 4;

    /// <summary>Main-bag cells use wire slots from 50 up; wire slots below it are the worn equipment slots.</summary>
    private const int MainBagFirstWireSlot = 50;

    // ---- Refinable gear and 2997 wire values ----

    /// <summary>Lowest ItemInfo.Equip_Slot of a weapon/armour piece that can be refined.</summary>
    private const int FirstRefineableEquipSlot = 1;

    /// <summary>Highest ItemInfo.Equip_Slot of a weapon/armour piece that can be refined.</summary>
    private const int LastRefineableEquipSlot = 7;

    /// <summary>2997 result code 0: the outcome field is processed.</summary>
    private const ushort ResultSuccess = 0;

    /// <summary>2997 result code 1: the request is refused; the client shows an error and unlocks the refine window.</summary>
    private const ushort ResultRefused = 1;

    /// <summary>2997 outcome 1: the refine failed and the level is kept.</summary>
    private const uint OutcomeFailKept = 1;

    /// <summary>2997 outcome 3: the refine failed and the level dropped.</summary>
    private const uint OutcomeDropped = 3;

    /// <summary>REFINE_RATE.Announce value that triggers the server-wide refine announce.</summary>
    private const int AnnounceEnabled = 1;

    /// <summary>REFINE_RATE.Down_Type 1: a surviving failure can drop the item to its band floor.</summary>
    private const int DownTypeToBandFloor = 1;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        ulong equipUid, matUid, thirdUid;
        uint shopOwnerHandle;
        {
            // Lenient: the declared length is not compared; the third slot below is optional, so the
            // blob length, not the declared one, decides whether it is read.
            if (!payload.Span.TryGetCompactBlob(CompactLengthCheck.Lenient, out var blob)
                || blob.Length < MaterialUidEnd) return;                      // through material UID hi (+35..+38)
            shopOwnerHandle = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(OffShopOwnerHandle)); // +9 the refine shop's owner (remarks)
            equipUid = BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(OffEquipUid));   // +20 lo, +24 hi (EQUIP)
            matUid   = BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(OffMaterialUid));   // +31 lo, +35 hi (MATERIAL)
            // THIRD slot @+42 lo / +46 hi. This is the second powder, the protection catalyst (e.g.
            // Infinium); without reading it, Infinium would never protect and a failed high refine would
            // destroy the item.
            thirdUid = blob.Length >= ThirdUidEnd ? BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(OffThirdUid)) : 0;
        }

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var store = services.GetRequiredService<GameDataStore>();
        var equip = await inventory.GetByIdAsync(equipUid);
        var mat = matUid != 0 ? await inventory.GetByIdAsync(matUid) : null;
        var third = (thirdUid != 0 && thirdUid != matUid && thirdUid != equipUid)
            ? await inventory.GetByIdAsync(thirdUid) : null;

        // ---- Validation (reject = 2997 with a non-zero result code: shows an error + unlocks the window) ----
        if (equip is null || equip.CharacterId != player.CharacterId || equipUid == matUid)
        {
            await RejectAsync(session, player); return;
        }
        // The equip must be a weapon/armour piece (ItemInfo.Equip_Slot 1..7). The same range is
        // used by the transfer window's gate (RefineTransferRules). Without any gate a stackable or
        // material could be refined to +20 for Need_Gold. Checked before any gold or catalyst is taken.
        if (!IsRefineableEquipSlot(store.GetItem(equip.ItemId)?.Equip_Slot ?? 0)) { await RejectAsync(session, player); return; }
        int cur = equip.Refine;
        int target = cur + 1;
        if (target > MaxRefineOf(store)) { await RejectAsync(session, player); return; }

        var rate = FindRate(store, target);
        if (rate is null) { await RejectAsync(session, player); return; }

        // Material is REQUIRED and must be a known REFINE_ITEM catalyst (plain ore is a valid all-zero row).
        // StorageType 0 (StorageBag) = the bag: a worn or banked catalyst is refused (consuming it would ghost its cell).
        if (mat is null || mat.CharacterId != player.CharacterId || mat.StorageType != BankSwapItemHandler.StorageBag) { await RejectAsync(session, player); return; }
        var matRow = FindMaterial(store, mat.ItemId);
        if (matRow is null) { await RejectAsync(session, player); return; }

        // THIRD slot = an OPTIONAL second powder (protection catalyst, e.g. Infinium). Fold its
        // REFINE_ITEM rates into the roll below (Break_Rate adds to survival). Ignored if absent or
        // not a known catalyst; both matRow and protRow contribute, so it does not matter which slot the
        // client used for which powder.
        var protRow = (third is not null && third.CharacterId == player.CharacterId)
            ? FindMaterial(store, third.ItemId) : null;
        // An owned third powder that is worn or banked is a forged request (the window only offers bag items): refuse
        // it whole, before anything is taken, as for the material.
        if (third is not null && third.CharacterId == player.CharacterId && third.StorageType != BankSwapItemHandler.StorageBag)
        { await RejectAsync(session, player); return; }

        long cost = rate.Need_Gold;
        if (!player.TryDebit(cost)) { await RejectAsync(session, player); return; }   // taken in the same atomic step that checks it

        // ---- Consume zeny + one catalyst up front (best-effort: on success AND failure) ----
        long newGold = player.Gold;
        await services.GetRequiredService<ICharacterRepository>().UpdateGoldAsync(player.CharacterId, newGold);
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnZenyChange,
            ActorVitalsPackets.BuildZenyChange2435(player.EntityId, newGold));

        // Map the catalyst to its client (bag, slot) and push the real bag update (2329 OnUseItem) so the
        // consumed catalyst's count drops / its slot clears; without this it would ghost in the bag.
        // remainingCount 0 clears the cell.
        (byte matBag, byte matSlot) = ClientBagSlot(mat);
        int matRemaining = mat.Count - 1;
        if (matRemaining <= 0) { await inventory.RemoveItemAsync(matUid); matRemaining = 0; }
        else { mat.Count = matRemaining; await inventory.UpdateItemAsync(mat); }
        await session.SendRmiAsync(ZoneS2COpcodes.OnUseItem,
            w => w.WriteBytes(CombatPackets.BuildUseItem2329(player.EntityId, matSlot, matBag, matUid,
                (byte)Math.Clamp(matRemaining, 0, byte.MaxValue), useSource: CombatPackets.UseSourceItemUse)));

        // Consume the THIRD powder (if any) the same way, so the protection catalyst (Infinium) drops from
        // the bag and its rates apply below.
        if (protRow is not null && third is not null)
        {
            (byte tBag, byte tSlot) = ClientBagSlot(third);
            int tRemaining = third.Count - 1;
            if (tRemaining <= 0) { await inventory.RemoveItemAsync(thirdUid); tRemaining = 0; }
            else { third.Count = tRemaining; await inventory.UpdateItemAsync(third); }
            await session.SendRmiAsync(ZoneS2COpcodes.OnUseItem,
                w => w.WriteBytes(CombatPackets.BuildUseItem2329(player.EntityId, tSlot, tBag, thirdUid,
                    (byte)Math.Clamp(tRemaining, 0, byte.MaxValue), useSource: CombatPackets.UseSourceItemUse)));
        }

        // ---- Roll the outcome: a direct reading of the REFINE_RATE / REFINE_ITEM columns (see RollRefine).
        // The item is never destroyed. ----
        var roll = RollRefine(rate, matRow, protRow, cur, MaxRefineOf(store), Random.Shared);
        string outcome = roll.Outcome;
        uint outcomeCode = roll.Code;
        int newLevel = roll.NewLevel;
        int succ = roll.SuccChance;
        if (newLevel != cur) equip.Refine = (byte)newLevel;
        // Embedded at 2997 blob+15 so the window shows the result and sets item+562.
        byte[] record126 = WorldEntryService.BuildItemRecord126(equip);
        if (newLevel != cur) await inventory.UpdateItemAsync(equip);

        // ---- Reflect the result (the refine bonus is StatService-computed from the
        // persisted Refine byte). Recompute the sheet regardless of outcome so a downgraded equipped
        // item's stats also update; broadcast the equip-view so a refined worn item shows its new +N. ----
        if (player.Character is { } ch)
        {
            var worn = await inventory.GetEquipmentAsync(ch.Id);
            // With the active effects folded in; sets the runtime max/current and the combat cache from the
            // same sheet (EffectService.RefreshPcTraitSheet).
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPcTrait,
                services.GetRequiredService<EffectService>().RefreshPcTraitSheet(player, ch, worn));
            (player.WeaponMinDamage, player.WeaponMaxDamage, player.AutoAttackRange) = StatService.WeaponDamageRange(store, worn);

            // The client expects the PC's 2315 after every 2848.
            await EquipViewNotifyPackets.BroadcastEquipChangeAsync(Broadcast, player, worn, services.GetRequiredService<IGuildEmblemRepository>());
        }

        // Answer 2997 with the FULL outcome record so the window shows the result and updates item+562 in
        // place (bag + paperdoll). aux = the old (pre-refine) level: the client reads blob+14 as the "from"
        // in the "(L -> L+1)" chat, so without it every message would render "(0 -> N)". The embedded
        // 126-byte record carries the refined item's KharaID/template/bag/slot and the new level
        // (record+0x1D = blob+44). (Stat bonus still comes from the 2498 re-push above.)
        await session.SendRmiAsync(ZoneS2COpcodes.OnRefineItem,
            w => w.WriteBytes(ShopItemPackets.BuildRefineItem2997(player.EntityId, ResultSuccess, outcomeCode, record126, aux: (byte)cur)));

        // 3005 to the refine shop's owner. +15 is the grade reached on a success, and on a failure
        // that keeps the grade the grade that was tried (17 -> 18); a downgrade reports
        // the grade it fell to, the one the 2997 record carries.
        await ReportToShopOwnerAsync(player, shopOwnerHandle, outcomeCode, cur,
            outcomeCode == OutcomeFailKept ? target : newLevel, equip.ItemId);

        // Server-wide 2894 refine announce (kind 8), sent only for successes (the new grade is higher than the
        // old one). The announce gate comes from the data: only the REFINE_RATE rows with Announce = 1 (grades
        // 18, 19 and 20) trigger it, so there is no hardcoded threshold. We check the row of the grade
        // REACHED, not of target = cur+1; the two readings differ only on a lucky jump that crosses into
        // the band (e.g. 16->18). The server name is taken from config. It goes to every player.
        if (outcomeCode == ShopItemPackets.RefineOutcomeSuccess && FindRate(store, newLevel)?.Announce == AnnounceEnabled)
        {
            var serverName = services.GetRequiredService<ServerConfiguration>().World.ServerName;
            var notice = CountLimitNoticePackets.BuildRefineAnnounce2894(serverName, player.Name, equip.ItemId, cur, newLevel);
            await Broadcast.BroadcastToAllAsync(ZoneS2COpcodes.NfyCountLimitItemNotice, w => w.WriteBytes(notice));
        }

        // (No destroy path anymore: the item is never removed, so the bag never needs the 2379 OnDestroyItem
        // clear. A drop to the band floor stays in place and its new level rides the 2997 record above.)

        Logger.LogInformation("{Name} refine {Item} {Cur}->{New}: {Outcome} (mat {Mat}, cost {Cost}, succ {Succ}%)",
            player.Name, equipUid, cur, newLevel, outcome, mat.ItemId, cost, succ / 100);
    }

    /// <summary>
    /// Tell the owner of the refine shop the buyer refined in (1668 blob+9) how the refine went, with 3005
    /// OnRefineItemToSeller on the owner's session (unencrypted). Nothing is sent when blob+9
    /// is the buyer itself, names no player in this zone, names a shop the buyer does not have open, or a
    /// shop without the refine service (see remarks).
    /// </summary>
    private async Task ReportToShopOwnerAsync(PlayerState buyer, uint ownerHandle, uint outcome, int oldGrade,
        int newGrade, uint itemTemplateId)
    {
        if (ownerHandle == 0 || ownerHandle == buyer.EntityId) return;
        var owner = PlayerManager.GetAllPlayers().FirstOrDefault(p => p.EntityId == ownerHandle);
        if (owner is null || !PrivateShopRegistry.IsViewer(owner, buyer)
            || !BeginRefineShopHandler.TryGetRefineService(owner, out var service) || service == 0)
            return;
        var ownerSession = Broadcast.GetSession(owner.SessionId);
        if (ownerSession is null) return;
        var body = RefineToSellerPackets.BuildRefineItemToSeller3005(owner.EntityId, outcome,
            (byte)Math.Clamp(oldGrade, 0, byte.MaxValue), (byte)Math.Clamp(newGrade, 0, byte.MaxValue), itemTemplateId, buyer.Name);
        await ownerSession.SendInternalRmiAsync(ZoneS2COpcodes.OnRefineItemToSeller, body);
        Logger.LogInformation("{Buyer} refined in {Owner}'s refine shop: outcome {Outcome} ({Old}->{New}), 3005 sent to the owner",
            buyer.Name, owner.Name, outcome, oldGrade, newGrade);
    }

    /// <summary>Reject the attempt: send a 2997 with a non-zero result code so the client shows a refine
    /// error and re-enables the (otherwise locked) refine window. Nothing is consumed.</summary>
    private static Task RejectAsync(ProudSession session, PlayerState player) =>
        session.SendRmiAsync(ZoneS2COpcodes.OnRefineItem,
            w => w.WriteBytes(ShopItemPackets.BuildRefineItem2997(player.EntityId, resultCode: ResultRefused)));

    /// <summary>Map an item's flat inventory slot to the client (bag, slot) — the SAME mapping as
    /// WorldEntryService.WriteItemInstance, so the 2329/2379 bag updates hit the exact cell the client drew.</summary>
    private static (byte bag, byte slot) ClientBagSlot(RO2.Domain.Entities.InventoryItem it)
    {
        if (it.StorageType == BankSwapItemHandler.StorageWorn)   // worn gear: container 0, slot = equip body-part
            return (EquipmentContainer, (byte)Math.Clamp(it.Slot < 0 ? 0 : (int)it.Slot, 0, MainBagFirstWireSlot - 1));
        if (it.StorageType == BankSwapItemHandler.StorageBank)   // bank/storage window is container 5
            return (BankPackets.BankBag, (byte)Math.Clamp(it.Slot < 0 ? 0 : (int)it.Slot, 0, BankExpansion.MaxBankSlots - 1));
        var flat = it.Slot < 0 ? 0 : (int)it.Slot;   // inventory: main bag = slots 50-69, extra bags 2-4 = 0-34
        if (flat < BagCapacity.MainBagCells) return (MainBagContainer, (byte)(MainBagFirstWireSlot + flat));
        var i = flat - BagCapacity.MainBagCells;
        return ((byte)Math.Clamp(FirstExtraBagContainer + i / BagCapacity.ExtraBagStride, FirstExtraBagContainer, LastExtraBagContainer),
            (byte)(i % BagCapacity.ExtraBagStride));
    }

    /// <summary>Weapon/armour Equip_Slot range (see HandleAsync).</summary>
    internal static bool IsRefineableEquipSlot(int equipSlot) => equipSlot is >= FirstRefineableEquipSlot and <= LastRefineableEquipSlot;

    /// <summary>The highest refine level the table reaches: its largest INDEX (INDEX = target level).</summary>
    private static int MaxRefineOf(GameDataStore store)
    {
        int max = 0;
        foreach (var r in store.REFINERATE) if (r.INDEX > max) max = r.INDEX;
        return max;
    }

    /// <summary>Result of one refine roll: the 2997 outcome code (0 success, 1 kept, 3 drop), the level reached,
    /// a log label, and the success chance used (0..10000).</summary>
    internal readonly record struct RefineRollResult(uint Code, int NewLevel, string Outcome, int SuccChance);

    /// <summary>The upper band starts at +11 (its floor is +10); a lower-band item falls back to +0.</summary>
    private const int UpperBandStart = 11, UpperBandFloor = 10;

    /// <summary>The floor of the lower band (+0 to +10).</summary>
    private const int LowerBandFloor = 0;

    /// <summary>Rates are in units of 1/10000: 10000 is a certainty.</summary>
    private const int RateScale = 10000;

    /// <summary>Levels gained by a plain success, and by the lucky 2-level and 3-level jumps.</summary>
    private const int GradeJumpSingle = 1, GradeJumpDouble = 2, GradeJumpTriple = 3;

    /// <summary>
    /// The refine roll, reading each column as named (all rates /10000):
    /// <list type="bullet">
    /// <item>success = clamp(<c>Succ_Rate</c> + catalysts' <c>Succ_Rate</c>, 0, 10000). On success the level rises by 1,
    ///   or by a lucky 3 / 2 from the catalysts' <c>3_GRADE_RATE</c> / <c>2_GRADE_RATE</c> (+3 rolled first, then +2),
    ///   never past <paramref name="maxRefine"/>.</item>
    /// <item>on failure, survival = clamp(<c>Break_Rate</c> + catalysts' <c>Break_Rate</c>, 0, 10000) (Infinium 10000 =
    ///   always survives). The original rules destroy the item when survival fails; this server never destroys, so that
    ///   case resets the item to its band floor instead.</item>
    /// <item>if it survived, <c>Down_Rate</c> is the chance it still drops. <c>Down_Type</c> 1 = drop to the band
    ///   floor (+10 from +11 or more, else +0; never below it), not -1. Any other <c>Down_Type</c> (the table has
    ///   only 1) means no drop. Otherwise the level is kept.</item>
    /// </list>
    /// The catalyst rows' <c>Down_Rate</c> is 0 in every row of REFINE_ITEM and is not read.
    /// </summary>
    internal static RefineRollResult RollRefine(RO2.Domain.GameData.Tables.REFINERATE rate,
        RO2.Domain.GameData.Tables.REFINEITEM mat, RO2.Domain.GameData.Tables.REFINEITEM? prot,
        int cur, int maxRefine, Random rng)
    {
        int succ = Math.Clamp(rate.Succ_Rate + mat.Succ_Rate + (prot?.Succ_Rate ?? 0), 0, RateScale);
        if (rng.Next(RateScale) < succ)
        {
            // Roll +3 first, then +2, else +1. Plain catalysts have both grade rates 0, so always +1.
            int g3 = mat._3_GRADE_RATE + (prot?._3_GRADE_RATE ?? 0);
            int g2 = mat._2_GRADE_RATE + (prot?._2_GRADE_RATE ?? 0);
            int r = rng.Next(RateScale);
            int jump = Math.Min(r < g3 ? GradeJumpTriple : r < g3 + g2 ? GradeJumpDouble : GradeJumpSingle, maxRefine - cur);
            return new RefineRollResult(ShopItemPackets.RefineOutcomeSuccess, cur + jump, jump > GradeJumpSingle ? $"success +{jump}" : "success", succ);
        }

        int survive = Math.Clamp(rate.Break_Rate + mat.Break_Rate + (prot?.Break_Rate ?? 0), 0, RateScale);
        int floor = cur >= UpperBandStart ? UpperBandFloor : LowerBandFloor;
        bool dropped = rng.Next(RateScale) >= survive
            || (rate.Down_Type == DownTypeToBandFloor && rng.Next(RateScale) < rate.Down_Rate);
        if (dropped && floor < cur)
            return new RefineRollResult(OutcomeDropped, floor, "drop to band floor", succ);
        return new RefineRollResult(OutcomeFailKept, cur, "fail-kept", succ);
    }

    private static RO2.Domain.GameData.Tables.REFINERATE? FindRate(GameDataStore store, int targetGrade)
    {
        foreach (var r in store.REFINERATE) if (r.INDEX == targetGrade) return r;
        return null;
    }

    private static RO2.Domain.GameData.Tables.REFINEITEM? FindMaterial(GameDataStore store, uint materialTemplateId)
    {
        foreach (var m in store.REFINEITEM) if (m.ITEM_ID == materialTemplateId) return m;
        return null;
    }
}
