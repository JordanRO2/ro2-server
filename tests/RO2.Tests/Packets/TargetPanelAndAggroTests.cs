using System.Buffers.Binary;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Gates for the three pieces the target panel and the threat meter are built from: 2643
/// AnsActorIconInfo for an NPC, 2629 AnsAggroList's entry order, and the weapon slots that wear on a
/// dealt hit (2503). Every vector is a byte-exact body.
/// </summary>
public class TargetPanelAndAggroTests
{
    // ---- 2643 AnsActorIconInfo -------------------------------------------------------------

    /// <summary>
    /// An NPC panel with a live target-of-target: HP 2,803,202 / 3,076,439,
    /// SP 6,594 / 6,594, level 53.
    /// Pins the unaligned u32 at body+17 next to a non-zero SP pair.
    /// </summary>
    [Fact]
    public void ActorIconInfo2643_ReproducesAReferenceNpcPanelWithATarget()
    {
        var expected = Convert.FromHexString("1303602802c62a0057f12e00c219c219351142490b");
        var body = CombatPackets.BuildActorIconInfo2643(0x28600313u, 2803202, 3076439, 6594, 6594, 53,
            targetOfTarget: 0x0B494211u);
        Assert.Equal(expected, body);
    }

    /// <summary>
    /// The NPC panel restates the SP this server's own 2506 advertised (sp == maxSp) instead of 0/0, and
    /// names the monster's current target at body+17.
    /// </summary>
    [Fact]
    public void NpcPanel_CarriesTheAdvertisedSpAndTheCurrentTarget()
    {
        var npc = new NpcRuntimeState { EntityOid = 0x01636213u, Hp = 900, MaxHp = 1704, Level = 25 };
        npc.AggroTargetHandle = 0x06684611u;

        var body = ReqActorDetailInfoHandler.BuildIconOf(npc);

        Assert.Equal(CombatPackets.ActorIconBodyLen, body.Length);
        Assert.Equal(0x01636213u, BinaryPrimitives.ReadUInt32LittleEndian(body));
        Assert.Equal(900u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)));
        Assert.Equal(1704u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        var sp = (ushort)SkillDamagePackets.NpcTargetSp(npc);
        Assert.NotEqual(0, sp);
        Assert.Equal(sp, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(12)));
        Assert.Equal(sp, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(14)));
        Assert.Equal(25, body[16]);
        Assert.Equal(0x06684611u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(17)));
    }

    /// <summary>A dead monster's panel (threat cleared) reports HP 0 and no target.</summary>
    [Fact]
    public void NpcPanel_AfterDeathHasZeroHpAndNoTarget()
    {
        var npc = new NpcRuntimeState { EntityOid = 0x01636213u, Hp = 0, MaxHp = 1704, Level = 25, IsDead = true };

        var body = ReqActorDetailInfoHandler.BuildIconOf(npc);

        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(17)));
    }

    // ---- 2629 AnsAggroList -----------------------------------------------------------------

    /// <summary>
    /// A list whose current target is NOT the top row: a monster targets an attacker holding
    /// 590,060 threat while another holds 633,320 and a third holds 1. The target is written first and the
    /// rest threat-descending; the rows are inserted here in a different order to prove the snapshot
    /// sorts them.
    /// </summary>
    [Fact]
    public void AggroList2629_TargetFirstThenThreatDescending_ReproducesARetailFrame()
    {
        var expected = Convert.FromHexString(
            "136f2f3f116d090a010301116d090a0201084e0061006d0061006b00750072006f00ec000900" +
            "01116b090a02010a54007300750079006f00690046006f006b006100e8a90900" +
            "01116c090a0201074100720063006b0068006f006e0001000000");

        var npc = new NpcRuntimeState { EntityOid = 0x3F2F6F13u };
        npc.Threat[0x0A096C11u] = new NpcStateManager.ThreatRow { AttackerHandle = 0x0A096C11u, Name = "Arckhon", Threat = 1 };
        npc.Threat[0x0A096D11u] = new NpcStateManager.ThreatRow { AttackerHandle = 0x0A096D11u, Name = "Namakuro", Threat = 590060 };
        npc.Threat[0x0A096B11u] = new NpcStateManager.ThreatRow { AttackerHandle = 0x0A096B11u, Name = "TsuyoiFoka", Threat = 633320 };
        npc.AggroTargetHandle = 0x0A096D11u;

        var entries = OpenAggroListHandler.SnapshotThreat(npc, out var currentTarget);
        var body = AggroPackets.BuildAggroList2629(npc.EntityOid, currentTarget, entries);

        Assert.Equal(0x0A096D11u, currentTarget);
        Assert.Equal(expected, body);
    }

    /// <summary>An empty table answers with the 10-byte empty list and target 0.</summary>
    [Fact]
    public void AggroList2629_EmptyTableIsTheTenByteEmptyList()
    {
        var npc = new NpcRuntimeState { EntityOid = 0x01030D13u };

        var entries = OpenAggroListHandler.SnapshotThreat(npc, out var currentTarget);
        var body = AggroPackets.BuildAggroList2629(npc.EntityOid, currentTarget, entries);

        Assert.Equal(0u, currentTarget);
        Assert.Equal(Convert.FromHexString("130d0301000000000100"), body);
    }

    // ---- 2503 weapon wear ------------------------------------------------------------------

    /// <summary>
    /// Every worn Equip_Slot 6 and 7 item wears, main hand first, whatever order the repository
    /// returns them in; armour, accessories and unworn items do not.
    /// </summary>
    [Fact]
    public void WeaponWear_SelectsMainHandThenOffHandOnly()
    {
        var slots = new Dictionary<uint, ushort> { [16702305] = 7, [16702304] = 6, [20000001] = 1, [30000001] = 8 };
        ItemData? Lookup(uint id) => slots.TryGetValue(id, out var s) ? new ItemData { ID = id, Equip_Slot = s } : null;

        var offHand = new InventoryItem { Id = 1010000007926907218UL, ItemId = 16702305, StorageType = 1, Slot = 7 };
        var mainHand = new InventoryItem { Id = 1010000008357062659UL, ItemId = 16702304, StorageType = 1, Slot = 6 };
        var armour = new InventoryItem { Id = 1, ItemId = 20000001, StorageType = 1 };
        var accessory = new InventoryItem { Id = 2, ItemId = 30000001, StorageType = 1 };
        var bagWeapon = new InventoryItem { Id = 3, ItemId = 16702304, StorageType = 0 };
        var unknown = new InventoryItem { Id = 4, ItemId = 99999999, StorageType = 1 };

        var worn = DurabilityService.SelectWornWeapons(
            new[] { offHand, armour, bagWeapon, mainHand, accessory, unknown }, Lookup);

        Assert.Equal(new[] { mainHand, offHand }, worn);
        Assert.Equal((ushort)7, DurabilityService.WeaponOffHandSlot);
    }

    /// <summary>A main-hand-only character wears just that weapon, one 2503.</summary>
    [Fact]
    public void WeaponWear_MainHandOnlyWearsOneItem()
    {
        ItemData? Lookup(uint id) => id == 16702297 ? new ItemData { ID = id, Equip_Slot = 6 } : null;
        var weapon = new InventoryItem { Id = 1010000008308482508UL, ItemId = 16702297, StorageType = 1, Slot = 6 };

        Assert.Equal(new[] { weapon }, DurabilityService.SelectWornWeapons(new[] { weapon }, Lookup));
    }
}
