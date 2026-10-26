using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Worlds.Domain;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;
using RO2.Inventory.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// Item skills (Skill_ID_1 of 2,000,000 or more, such as potions, warp and revival wings and town scrolls) must start through RMI 1561 ReqSkillStart_Item, which verifies that the caster owns the item and consumes it before recording the pending cast. A raw 1508 ReqSkillStart for such a skill is refused, and the learned-skill check covers item skills. These tests pin the refusal and confirm that the 1561 path and the class-skill 1508 flow are unaffected.
/// </summary>
public class ItemSkillForgeryTests
{
    
    
    private const uint RevivalItemSkill = 9200101;
    private const uint ConsumableItemSkill = 9201001;
    private const uint ConsumableItemId = 21000001;
    private const uint ClassSkill = 412105;
    private const uint GuildEmergencyCall = 4000101;

    
    [Fact]
    public async Task ItemSkillSet_IsItemInfoSkillId1AtOrAbove2M_NotGuildOrClass()
    {
        var store = await LoadStoreAsync("ItemInfo.json");
        Assert.True(store.IsItemSkill(RevivalItemSkill));
        Assert.True(store.IsItemSkill(ConsumableItemSkill));
        Assert.False(store.IsItemSkill(ClassSkill));          
        Assert.False(store.IsItemSkill(GuildEmergencyCall));  
    }

    
    [Fact]
    public async Task ForgedItemSkill1508_IsRefusedSilently_RecordsNoPendingCast_And1509LandsNothing()
    {
        await using var net = await Harness.StartAsync(1);
        var players = new PlayerStateManager(new NoDirtySink());
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(net.Server);
        var sid = net.Sessions[0].SessionId;
        var caster = new PlayerState { SessionId = sid, EntityId = (sid << 8) | 0x11, MapId = 1100, MapLoaded = true,
            Level = 1, Hp = 500, Mp = 100 };
        players.AddPlayer(sid, caster);
        var collection = new ServiceCollection();
        collection.AddSingleton(await LoadStoreAsync("ItemInfo.json", "Skill_Table_SkillLevel.json"));
        await using var services = collection.BuildServiceProvider();
        var handler = new SkillStartHandler(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SkillStartHandler>.Instance, players, broadcast);

        
        var forged = new byte[47];
        BinaryPrimitives.WriteUInt32LittleEndian(forged, RevivalItemSkill);
        BinaryPrimitives.WriteUInt32LittleEndian(forged.AsSpan(16), 0x0ADEAD11u);   
        BinaryPrimitives.WriteUInt16LittleEndian(forged.AsSpan(45), 1);             

        await handler.HandleAsync(net.Sessions[0], forged);

        
        var refusal = await net.Readers[0].NextAsync();
        Assert.Equal(ZoneS2COpcodes.AnsUseSkillStart, refusal.Op);
        Assert.Equal(SkillPackets.SkillResultOnCooldown, BinaryPrimitives.ReadUInt16LittleEndian(refusal.Body));
        Assert.Equal(100, caster.Mp);

        
        await net.Sessions[0].SendInternalRmiAsync(ZoneS2COpcodes.OnLevelUp, new byte[] { 0x5A });
        Assert.Equal((ZoneS2COpcodes.OnLevelUp, "5a"), Hexed(await net.Readers[0].NextAsync()));

        
        
        Assert.Equal(0, SkillCastLandings.PendingCount(caster, DateTime.UtcNow));
        Assert.False(SkillCastLandings.TryGovern(caster, RevivalItemSkill, DateTime.UtcNow, consume: true, out _));
    }

    
    [Fact]
    public async Task LegitItemUse_RecordsThePendingCast_SoThe1509IsGoverned_AndConsumesTheItem()
    {
        await using var net = await Harness.StartAsync(1);
        var players = new PlayerStateManager(new NoDirtySink());
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(net.Server);
        var sid = net.Sessions[0].SessionId;
        var caster = new PlayerState { SessionId = sid, EntityId = (sid << 8) | 0x11, CharacterId = 4242,
            MapId = 1100, MapLoaded = true, Level = 10, Hp = 500, Mp = 500 };
        players.AddPlayer(sid, caster);

        const ulong uid = 0xA11CE5u;
        var bag = new GearInventory(new[]
        {
            new InventoryItem { Id = uid, CharacterId = caster.CharacterId, ItemId = ConsumableItemId, Count = 5,
                StorageType = 0, Slot = 0 },
        });
        var collection = new ServiceCollection();
        collection.AddSingleton(await LoadStoreAsync("ItemInfo.json", "Skill_Table_SkillLevel.json"));
        collection.AddSingleton<RO2.Inventory.Domain.IInventoryRepository>(bag);
        await using var services = collection.BuildServiceProvider();

        
        
        var store = services.GetRequiredService<GameDataStore>();
        Assert.True(store.IsItemSkill(ConsumableItemSkill));
        Assert.Equal(ItemWarpSkills.MoveKind.None, ItemWarpSkills.KindOf(ConsumableItemSkill));
        Assert.False(RevivalSkillPackets.IsRevivalSkill(ConsumableItemSkill));

        var handler = new SkillStartItemHandler(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SkillStartItemHandler>.Instance, players, broadcast);

        
        var request = new byte[55];
        BinaryPrimitives.WriteUInt32LittleEndian(request, ConsumableItemSkill);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(16), caster.EntityId);
        BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(47), uid);

        await handler.HandleAsync(net.Sessions[0], request);

        
        
        Assert.Equal(4, bag.Items.Single(i => i.Id == uid).Count);
        Assert.Equal(1, SkillCastLandings.PendingCount(caster, DateTime.UtcNow));
        Assert.True(SkillCastLandings.TryGovern(caster, ConsumableItemSkill, DateTime.UtcNow, consume: true, out var cast));
        Assert.Equal(ConsumableItemSkill, cast.SkillId);
    }

    
    [Fact]
    public void DamagingItemSkill_AtAnNpc_PassesTheGateByPossession_NotByLearnedSkills()
    {
        
        
        
        var noSkills = new GateActor(1100, 0, 0, false, new Dictionary<uint, byte>());
        var npc = new GateTarget(1100, 3, 0, false);

        Assert.Equal(ActorRefusal.None,
            SkillDamageHandler.EvaluateDamageGate(noSkills, ConsumableItemSkill, npc, reach: 10f));
        
        Assert.Equal(ActorRefusal.SkillNotLearned,
            SkillDamageHandler.EvaluateDamageGate(noSkills, ClassSkill, npc, reach: 10f));
    }

    [Fact]
    public void DamagingItemSkill_WithTheItemHeld_IsGoverned_WithoutIt_IsRefused()
    {
        var p = new PlayerState { MapId = 1100 };
        var now = DateTime.UtcNow;
        
        Assert.False(SkillCastLandings.TryGovern(p, ConsumableItemSkill, now, consume: true, out _));
        
        SkillCastLandings.RecordPending(p, ConsumableItemSkill, p.EntityId, now, TimeSpan.FromSeconds(5));
        Assert.True(SkillCastLandings.TryGovern(p, ConsumableItemSkill, now, consume: true, out _));
        Assert.False(SkillCastLandings.TryGovern(p, ConsumableItemSkill, now, consume: true, out _));
    }
}
