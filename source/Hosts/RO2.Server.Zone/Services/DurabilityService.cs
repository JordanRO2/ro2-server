using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Durability wear — the attacker's weapon-slot items on a dealt hit, the victim's armour on a hit
/// taken — and the RMI 2503 OnUpdateItemDurability the server sends to keep the owner's client in sync.
/// </summary>
/// <remarks>
/// <para>
/// The wire builder <see cref="ItemPackets.BuildItemDurability2503"/> builds the message (header
/// <c>01 12 1200 cde3 00000000</c> + u64 uid + u16 durability). This service is the trigger.
/// </para>
/// <para>
/// <b>Weapon slots, dealt hits.</b> Every worn item in Equip_Slot 6 AND 7 loses one point
/// per landed normal hit and per skill use, in ascending slot order, one 2503 each
/// (<see cref="ApplyWeaponWearOnHitAsync"/>). A skill wears the weapon slots whether or not it did damage, so
/// damage does not matter. A player with only a slot-6 weapon wears that one; a main-hand and its off-hand wear
/// as the pair (6, 7). Each step is -1.
/// </para>
/// <para>
/// The wearer-TAKES-a-hit case is <see cref="ApplyArmourWearOnTakenHitAsync"/>: one 2503 per worn
/// armour piece, by <see cref="ArmourWearStep"/> (-1 or -2, decided by the piece's Require_Job). Its callers are
/// the monster-retaliation pass (<see cref="NpcCombatService"/>), right after the 2696 of each hit it lands on a
/// player, the field-duel hit paths, and the skill landing (SkillDamageHandler) for every PC a 2718 LISTS.
/// </para>
/// <para>
/// <b>Armour, skills: the 2718's own element list is the selector</b>, no skill column. Every PC a 2718 lists as
/// an element (handle != 0, tag 0x11) has its armour worn, whoever the caster is and whatever the skill type or
/// effect kind; a miss (effect kind 2) on a listed PC still wears, unlike the 2696 path. The type-10 single
/// all-zero element whose request named one PC wears that named PC. Not listed, no wear. On the caster the
/// burst is armour first, then weapons. The step is the same <see cref="ArmourWearStep"/>.
/// <see cref="SkillWearPcHandles"/> is the rule. Not covered: heals (the protocol lists healed PCs, this server
/// lists none, having no heal model) and monster skill casts (no NPC skill casting here); the 1549 / 2797 tick
/// path is not modelled.
/// </para>
/// <para>
/// <b>Death wear</b> is <see cref="ApplyDeathWearAsync"/>: at every player death a 2503 is sent for each worn
/// record in slots 1..12, ascending, before the 2391 (whose records then carry the new values). See its remarks.
/// </para>
/// <para>
/// <b>The recipient is the item's OWNER, unicast, never a broadcast.</b> The 20-byte message carries
/// no actor handle — nothing in it says whose item this is — and the client resolves the
/// uid against the LOCAL player's own item store and inventory window. A broadcast copy would be
/// unattributable, and <see cref="BroadcastService.BroadcastToNearbyAsync"/> excludes the source session anyway.
/// </para>
/// </remarks>
public sealed class DurabilityService
{
    private readonly GameDataStore _data;
    private readonly ILogger<DurabilityService> _logger;

    /// <summary>
    /// ItemInfo.Equip_Slot of the main-hand weapon. In <c>data/ItemInfo.json</c>, almost every
    /// row with Equip_Slot 6 is Item_Type 1 (weapon).
    /// </summary>
    public const ushort WeaponMainHandSlot = 6;

    /// <summary>
    /// ItemInfo.Equip_Slot of the off-hand item (shield, doll, second weapon), which wears together
    /// with the main hand: weapon bursts of two items are (6, 7) (see the class remarks).
    /// </summary>
    public const ushort WeaponOffHandSlot = 7;

    /// <summary>
    /// Points each weapon-slot item loses per wearing hit or skill use (see the class remarks). The per-hit armour
    /// step is -1 or -2 (see <see cref="ArmourWearStep"/>).
    /// </summary>
    public const int WeaponWearPerHit = 1;

    /// <summary>
    /// Fallback maximum durability when the item template carries no positive Durability. 10000 is
    /// the typical ceiling of Equip_Slot 6 rows and the value 2503's wire field never exceeds.
    /// </summary>
    public const ushort DefaultMaxDurability = ItemPackets.DurabilityMax; // 10000

    /// <summary>
    /// ItemInfo.Equip_Slot range of the worn armour pieces that wear when their wearer is hit. In
    /// <c>data/ItemInfo.json</c> slots 1..5 hold exactly the Item_Type 2 (armour) rows, and the burst after a hit
    /// TAKEN is these five slots in ascending Equip_Slot order, while the weapon slots 6/7 wear on hits DEALT.
    /// Accessory slots (8..12) do not wear per hit.
    /// </summary>
    public const ushort ArmourFirstSlot = 1;

    /// <inheritdoc cref="ArmourFirstSlot"/>
    public const ushort ArmourLastSlot = 5;

    /// <summary>ItemInfo.Require_Job bits of job 17 (Crescentia, bit 16) and job 18 (Soul Maker, bit 17): the
    /// armour pieces whose mask holds either wear by 1 per hit taken, every other armour by 2
    /// (<see cref="ArmourWearStep"/>).</summary>
    public const long SlowArmourWearJobMask = (1L << CrescentiaJobBit) | (1L << SoulMakerJobBit);

    /// <summary>Bit index in ItemInfo.Require_Job of job 17 (Crescentia).</summary>
    private const int CrescentiaJobBit = 16;

    /// <summary>Bit index in ItemInfo.Require_Job of job 18 (Soul Maker).</summary>
    private const int SoulMakerJobBit = 17;

    /// <summary>Points an armour piece of a slow-wear job loses per damaging hit taken (see <see cref="ArmourWearStep"/>).</summary>
    private const int SlowArmourWearStep = 1;

    /// <summary>Points any other armour piece loses per damaging hit taken (see <see cref="ArmourWearStep"/>).</summary>
    private const int ArmourWearStepDefault = 2;

    /// <summary>InventoryItem.StorageType of a worn (equipped) row.</summary>
    private const byte StorageEquipped = 1;

    /// <summary>Mask of the actor type tag, the low byte of an actor handle (a PC carries <see cref="PlayerStateManager.PcActorTag"/>).</summary>
    private const uint ActorTagMask = 0xFF;

    /// <summary>
    /// Points one worn armour piece loses per damaging hit taken. The pieces whose Require_Job holds job 17 or 18
    /// (<see cref="SlowArmourWearJobMask"/>; masks 65536, 131072, 196608, 229376) step -1; every other armour steps
    /// -2. These pieces are job-locked, so "item mask" and "wearer job" cannot be told apart on the wire; the item
    /// mask is used. Weapons (slots 6/7) step -1 whatever the job, which <see cref="WeaponWearPerHit"/> already
    /// does.
    /// </summary>
    public static int ArmourWearStep(ItemData info) => (info.Require_Job & SlowArmourWearJobMask) != 0 ? SlowArmourWearStep : ArmourWearStepDefault;

    /// <summary>Death wear on a Map_PK 0 map: <c>max(0, cur - 950)</c> per worn slot-1..12 item. The floor at 0 is
    /// real (a piece below 950 drops to 0). Every item takes 950 whatever its Grade, Item_Lv or Require_Level.</summary>
    public const int DeathWearLoss = 950;

    /// <summary>Worn slots that carry the death-wear burst: armour 1-5, weapons 6-7, accessories 8-12. Slot 13 (Item_Type
    /// 104), the costumes in 14-19 and the cards in 20+ are never in it.</summary>
    public const short DeathWearFirstSlot = 1;

    /// <inheritdoc cref="DeathWearFirstSlot"/>
    public const short DeathWearLastSlot = 12;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BroadcastService _broadcast;

    public DurabilityService(GameDataStore data, ILogger<DurabilityService> logger,
        IServiceScopeFactory scopeFactory, BroadcastService broadcast)
    {
        _data = data;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _broadcast = broadcast;
    }

    /// <summary>
    /// Wear every armour piece the victim has on by its <see cref="ArmourWearStep"/>, persist it,
    /// and send one RMI 2503 per piece to the VICTIM's own session.
    /// </summary>
    /// <param name="victim">The player who just took a damaging hit, or whom a 2718 just listed.</param>
    /// <remarks>
    /// <para>
    /// After a 2696, call this ONLY for a hit that did damage (a 2696 miss wears nothing), and
    /// after the 2696 that reports it: the burst follows the victim's 2696 in the same instant, before any other
    /// combat message.
    /// </para>
    /// <para>
    /// It is also the skill-listed-PC wear: SkillDamageHandler calls it, after the 2718, for every PC
    /// <see cref="SkillWearPcHandles"/> names, a missed element included (class remarks).
    /// </para>
    /// <para>
    /// Unlike the weapon path, a piece already at 0 is still reported at 0, so the client keeps showing it as
    /// broken rather than stale.
    /// </para>
    /// <para>
    /// The current value of an unset (-1) row is its template's Durability (<see cref="UnsetDurabilityOf"/>), as
    /// <see cref="ApplyDeathWearAsync"/> and the 2310/2391 record writer report it. This used to take
    /// <see cref="DefaultMaxDurability"/> for a template-0 piece, so it wrote and sent an invented 9998. With the
    /// template value such a piece goes out at 0 and is not rewritten. The handling of these pieces on a HIT is
    /// not established: some ItemInfo rows in Equip_Slot 1..5 have Durability 0. The rule follows the death wear,
    /// where template-0 accessories go out at 0.
    /// </para>
    /// <para>
    /// Built for callers without a request scope (the NPC retaliation tick): the victim's session and
    /// a scoped <see cref="IInventoryRepository"/> are resolved here. Failures are logged and swallowed
    /// for the same reason as <see cref="ApplyWeaponWearOnHitAsync"/>.
    /// </para>
    /// </remarks>
    public async Task ApplyArmourWearOnTakenHitAsync(PlayerState victim, CancellationToken ct = default)
    {
        var session = _broadcast.GetSession(victim.SessionId);
        if (session is null || !session.IsConnected)
            return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var inventory = scope.ServiceProvider.GetRequiredService<IInventoryRepository>();
            var equipment = await inventory.GetEquipmentAsync(victim.CharacterId);

            var armour = SelectWornArmour(equipment, _data.GetItem);

            foreach (var (_, piece, step) in armour)
            {
                int current = piece.Durability >= 0 ? piece.Durability : UnsetDurability(piece.ItemId);
                int updated = Math.Max(0, current - step);
                if (updated != current)
                {
                    piece.Durability = (short)updated;
                    try
                    {
                        await inventory.UpdateItemAsync(piece);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Session {Id}: armour durability persist failed for item {Uid}",
                            session.SessionId, piece.Id);
                    }
                }

                var body = ItemPackets.BuildItemDurability2503(piece.Id, (ushort)updated);
                await session.SendRmiAsync(ZoneS2COpcodes.OnUpdateItemDurability,
                    w => w.WriteBytes(body), ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Session {Id}: armour durability wear failed", session.SessionId);
        }
    }

    /// <summary>
    /// Death wear: every worn item in slots <see cref="DeathWearFirstSlot"/>..<see cref="DeathWearLastSlot"/> loses
    /// <see cref="DeathWearLoss"/> points (none on a Map_PK map), is persisted, and is reported with one 2503 to the
    /// victim's own session, in ascending slot order. Call it at the death, after the 2938 / state-0 2398 when the
    /// path sends them and before the Khara failures (2581), the first state-6 2398 and the 2391.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Burst contents</b>: the victim's worn records in slots 1..12 in ascending worn slot. The slot is the
    /// worn slot, not ItemInfo.Equip_Slot: they differ for rings (earrings keep their slot; rings 10-&gt;{10,11}). An
    /// accessory whose template Durability is 0 is still sent, at 0, so the selector is the slot range, not
    /// ItemInfo.Durability &gt; 0. The 2391 record durability equals the burst value, so the burst is persisted
    /// before the 2391 reads the gear. Order on a field map:
    /// 2938 [01 00] -> 2398 state 0 -> this burst -> 2581 NfyKharaFailed* -> 2398 state 6 -> the lethal 2696/2495 ->
    /// that hit's armour 2503s -> 2391 (for a fall: 2938 -> s0 -> burst -> 2574/2577 -> s6 -> 2815 -> 2391).
    /// </para>
    /// <para>
    /// <b>PK maps.</b> On the Map_PK 1 maps (3101, 3201, 4109, 9103) the full slot 1..12 burst is STILL sent, with
    /// the values unchanged (a step of 0). Map_PK 1 lines up exactly with the state-6 2398's blob+15 == 1. So the
    /// burst always goes out and only the loss
    /// depends on the map.
    /// </para>
    /// <para>
    /// The current value of an unset (-1) row is its template's Durability, as the 2310/2391 record writer reports it
    /// (WorldEntryService.WriteItemInstance), so a template-0 accessory goes out at 0 and is not rewritten. Built for
    /// callers without a request scope (the NPC retaliation tick); failures are logged and swallowed, like the other
    /// wear paths, so a death is never lost over a durability write.
    /// </para>
    /// </remarks>
    public async Task ApplyDeathWearAsync(PlayerState victim, CancellationToken ct = default)
    {
        var session = _broadcast.GetSession(victim.SessionId);
        if (session is null || !session.IsConnected)
            return;

        int loss = DeathWearLossOn(_data, victim.MapId);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var inventory = scope.ServiceProvider.GetRequiredService<IInventoryRepository>();
            var equipment = await inventory.GetEquipmentAsync(victim.CharacterId);
            foreach (var item in SelectDeathWearItems(equipment))
            {
                int current = item.Durability >= 0 ? item.Durability : UnsetDurability(item.ItemId);
                int updated = DeathWearValue(current, loss);
                if (updated != item.Durability)
                {
                    item.Durability = (short)updated;
                    try
                    {
                        await inventory.UpdateItemAsync(item);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Session {Id}: death-wear persist failed for item {Uid}",
                            session.SessionId, item.Id);
                    }
                }
                var body = ItemPackets.BuildItemDurability2503(item.Id, (ushort)updated);
                await session.SendRmiAsync(ZoneS2COpcodes.OnUpdateItemDurability, w => w.WriteBytes(body), ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Session {Id}: death durability wear failed", session.SessionId);
        }
    }

    /// <summary>The death-wear loss on <paramref name="mapId"/>: 0 on a Map_PK map, <see cref="DeathWearLoss"/>
    /// elsewhere (a map the table does not know counts as non-PK).</summary>
    internal static int DeathWearLossOn(GameDataStore data, uint mapId)
        => data.GetMap(mapId) is { Map_PK: not 0 } ? 0 : DeathWearLoss;

    /// <summary>One item's value after a death: <c>max(0, current - loss)</c>, clamped to the u16 wire field.</summary>
    internal static int DeathWearValue(int current, int loss) => Math.Clamp(current - loss, 0, ushort.MaxValue);

    /// <summary>The worn items (StorageType 1) in slots 1..12, ascending by worn slot: the death-wear burst.</summary>
    internal static List<InventoryItem> SelectDeathWearItems(IReadOnlyList<InventoryItem> equipment)
        => equipment.Where(i => i.StorageType == StorageEquipped && i.Slot >= DeathWearFirstSlot && i.Slot <= DeathWearLastSlot)
            .OrderBy(i => i.Slot).ToList();

    /// <summary>The durability an unset (-1) row stands for: its template's Durability (0 for a template with none),
    /// or <see cref="DefaultMaxDurability"/> when no template is known, as the item record writer reports it.</summary>
    private ushort UnsetDurability(uint itemId) => UnsetDurabilityOf(_data.GetItem(itemId));

    /// <summary>The durability an unset (-1) row of <paramref name="template"/> stands for (see
    /// <see cref="UnsetDurability"/>): the template's Durability, 0 included, or <see cref="DefaultMaxDurability"/>
    /// when there is no template.</summary>
    internal static ushort UnsetDurabilityOf(ItemData? template)
        => template is { } t ? (ushort)Math.Clamp((long)t.Durability, 0, ushort.MaxValue) : DefaultMaxDurability;

    /// <summary>
    /// The PCs a landed skill's 2718 wears the armour of: in element order and de-duplicated, every element handle
    /// that is non-zero and carries the PC tag (<see cref="PlayerStateManager.PcActorTag"/>), plus, for a type-10
    /// single all-zero element cast (<see cref="SkillDamagePcElements.IsSingleZeroElementCast"/>), the one PC its
    /// request named. See the class remarks (the element list is the selector, whatever the skill type,
    /// caster or effect kind).
    /// </summary>
    /// <param name="hits">The 2718's elements as sent.</param>
    /// <param name="singleZeroCast">True when the 2718 is the single all-zero element of a type-10 cast at one PC.</param>
    /// <param name="named">The request's named actors (primary target, then the hit list); only read when
    /// <paramref name="singleZeroCast"/> is true, when it holds exactly that one PC.</param>
    internal static List<uint> SkillWearPcHandles(IReadOnlyList<SkillDamageHit> hits, bool singleZeroCast,
        IReadOnlyList<uint> named)
    {
        var handles = new List<uint>();
        foreach (var hit in hits)
        {
            var h = hit.TargetHandle;
            if (h != 0 && (h & ActorTagMask) == PlayerStateManager.PcActorTag && !handles.Contains(h))
                handles.Add(h);
        }
        if (singleZeroCast && named.Count > 0 && (named[0] & ActorTagMask) == PlayerStateManager.PcActorTag
            && !handles.Contains(named[0]))
            handles.Add(named[0]);
        return handles;
    }

    /// <summary>
    /// Wear every weapon-slot item the attacker has on (Equip_Slot <see cref="WeaponMainHandSlot"/>,
    /// then <see cref="WeaponOffHandSlot"/>) by <see cref="WeaponWearPerHit"/>, persist each new value
    /// and send one RMI 2503 OnUpdateItemDurability per item to the attacker's own session.
    /// </summary>
    /// <param name="session">The attacker's session — the sole recipient (unicast).</param>
    /// <param name="player">The attacker's runtime state; only <see cref="PlayerState.CharacterId"/> is used.</param>
    /// <param name="services">
    /// A scoped service provider (the one the RMI handler already created) from which the per-request
    /// <see cref="IInventoryRepository"/> is resolved.
    /// </param>
    /// <remarks>
    /// <para>
    /// Call it after the 2696 of every normal hit that is not a miss (NormalAttackDamageHandler does),
    /// and after the 2718 of every skill use, damaging or not (see the class remarks). For a skill it goes out
    /// before the state-3 2398 that the hit may start. Slot order is ascending because two-item bursts are (6, 7).
    /// </para>
    /// <para>
    /// An item already at 0 is skipped and nothing is sent for it: a weapon reaching 0 is not a defined case,
    /// so the behaviour there is this server's choice. Any repository failure is swallowed and
    /// logged: durability is not worth dropping the combat tick over, and the client is still told the
    /// new value so its tooltip stays in sync even if the write was lost.
    /// </para>
    /// </remarks>
    public async Task ApplyWeaponWearOnHitAsync(
        ProudSession session,
        PlayerState player,
        IServiceProvider services,
        CancellationToken ct = default)
    {
        IInventoryRepository inventory;
        try
        {
            inventory = services.GetRequiredService<IInventoryRepository>();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Session {Id}: no inventory repository for durability wear",
                session.SessionId);
            return;
        }

        List<InventoryItem> weapons;
        try
        {
            var equipment = await inventory.GetEquipmentAsync(player.CharacterId);
            weapons = SelectWornWeapons(equipment, _data.GetItem);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Session {Id}: equipment lookup failed for durability wear",
                session.SessionId);
            return;
        }

        // Unarmed, or no weapon template we recognise: nothing wears, so nothing is sent: the
        // message is per worn item.
        foreach (var weapon in weapons)
        {
            var max = ResolveMaxDurability(weapon.ItemId);

            // Durability -1 in the row means "unset / full", matching BuildItemRecord126's own
            // `Durability >= 0 ? Durability : 10000` convention.
            int current = weapon.Durability >= 0 ? weapon.Durability : max;
            if (current <= 0)
                continue; // already broken; repairs (increases) are not modelled here

            int updated = Math.Max(0, current - WeaponWearPerHit);
            weapon.Durability = (short)updated;

            try
            {
                await inventory.UpdateItemAsync(weapon);
            }
            catch (Exception ex)
            {
                // Notify the client anyway: a lost write is a resync-on-relog problem, a silent desync
                // is a worse one.
                _logger.LogDebug(ex, "Session {Id}: durability persist failed for item {Uid}",
                    session.SessionId, weapon.Id);
            }

            var body = ItemPackets.BuildItemDurability2503(weapon.Id, (ushort)updated);
            await session.SendRmiAsync(ZoneS2COpcodes.OnUpdateItemDurability,
                w => w.WriteBytes(body), ct);

            _logger.LogDebug("Session {Id}: weapon {Uid} durability {Old} -> {New} (max {Max})",
                session.SessionId, weapon.Id, current, updated, max);
        }
    }

    /// <summary>
    /// True when the row is worn (StorageType 1) in a cell its template may occupy
    /// (<see cref="EquipRules.WornSlotsFor"/>). A worn row in an illegal cell (stored before the 1324 legality gate, or
    /// by a path that skipped it) is a ghost: it is not in the category its template names, so it neither wears nor
    /// counts as that slot's item (StatService already ignores such a row).
    /// </summary>
    internal static bool IsWornInLegalCell(InventoryItem item, ItemData info)
        => item.StorageType == StorageEquipped && EquipRules.WornSlotsFor(info).Contains(item.Slot);

    /// <summary>
    /// The worn armour pieces that wear when their wearer is hit (template Equip_Slot
    /// <see cref="ArmourFirstSlot"/>..<see cref="ArmourLastSlot"/>, and standing in a cell their template may occupy,
    /// <see cref="IsWornInLegalCell"/>), ascending by Equip_Slot, with their <see cref="ArmourWearStep"/>.
    /// </summary>
    internal static List<(ushort Slot, InventoryItem Item, int Step)> SelectWornArmour(
        IReadOnlyList<InventoryItem> equipment, Func<uint, ItemData?> getItem)
    {
        var armour = new List<(ushort Slot, InventoryItem Item, int Step)>();
        foreach (var item in equipment)
        {
            if (item.StorageType != StorageEquipped)
                continue;
            var info = getItem(item.ItemId);
            if (info is null || info.Equip_Slot < ArmourFirstSlot || info.Equip_Slot > ArmourLastSlot
                || !IsWornInLegalCell(item, info))
                continue;
            armour.Add((info.Equip_Slot, item, ArmourWearStep(info)));
        }
        armour.Sort((a, b) => a.Slot.CompareTo(b.Slot));
        return armour;
    }

    /// <summary>
    /// The equipped items whose templates sit in <see cref="WeaponMainHandSlot"/> or
    /// <see cref="WeaponOffHandSlot"/>, main hand first, and that stand in a cell their template may occupy
    /// (<see cref="IsWornInLegalCell"/>). Ordered by the template's Equip_Slot, so it does not depend on how the
    /// instance's slot is encoded beyond the legal-cell test.
    /// </summary>
    /// <param name="equipment">The character's equipment rows; only StorageType 1 (worn) counts.</param>
    /// <param name="getItem">The ItemInfo lookup (<see cref="GameDataStore.GetItem"/> on the live path).</param>
    internal static List<InventoryItem> SelectWornWeapons(IReadOnlyList<InventoryItem> equipment,
        Func<uint, ItemData?> getItem)
    {
        var worn = new List<(ushort Slot, InventoryItem Item)>();
        foreach (var item in equipment)
        {
            if (item.StorageType != StorageEquipped)
                continue;
            var info = getItem(item.ItemId);
            if (info is null)
                continue;
            if ((info.Equip_Slot is WeaponMainHandSlot or WeaponOffHandSlot) && IsWornInLegalCell(item, info))
                worn.Add((info.Equip_Slot, item));
        }
        // Stable, so two items of one slot keep the repository's order.
        return worn.OrderBy(w => w.Slot).Select(w => w.Item).ToList();
    }

    /// <summary>
    /// Template maximum durability, clamped into the u16 the wire field is. Falls back to
    /// <see cref="DefaultMaxDurability"/> when the template has no positive Durability.
    /// </summary>
    private ushort ResolveMaxDurability(uint itemId)
    {
        var info = _data.GetItem(itemId);
        if (info is null || info.Durability == 0)
            return DefaultMaxDurability;
        return (ushort)Math.Min(info.Durability, ushort.MaxValue);
    }
}
