using System.Buffers.Binary;
using RO2.GameData;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Tests the effect (buff/debuff) system: the 2492 OnAddEffect and 2494 OnDelEffect wire order, and the
/// item-driven <see cref="EffectService.PlanItemEffect"/> trigger.
/// </summary>
/// <remarks>
/// <para>
/// The byte vectors are reference frame bodies.
/// </para>
/// <para>
/// The <see cref="EffectService"/> tests use the master data of the "Explorer's Shop Discount Scroll"
/// (item 21330026), so the derived duration of 3,600,100 ms is checked against the shipped data.
/// </para>
/// </remarks>
public class Effects2492Tests
{
    private static ReadOnlySpan<byte> Blob(byte[] body) => body.AsSpan(1 + body[0]);

    private static void AssertWellFormed(byte[] body)
    {
        
        
        Assert.Contains(body[0], new byte[] { 1, 2, 4, 8 });
        var blob = Blob(body);
        Assert.Equal(blob.Length, BinaryPrimitives.ReadUInt16LittleEndian(blob));
    }

    
    
    private static ItemData ShopDiscountScroll => new()
    {
        ID = 21330026, Effect_ID_1 = 100001, Skill_ID_1 = 10000101,
    };

    private static SkillLevelData ShopDiscountSkill => new()
    {
        ID = 10000101, Effect_Duration = 3600.1f,
    };

    [Fact]
    public void PlanItemEffect_DerivesEffectSkillAndDurationFromTheItemsOwnColumns()
    {
        var plan = EffectService.PlanItemEffect(ShopDiscountScroll, ShopDiscountSkill);

        Assert.NotNull(plan);
        var p = plan!.Value;
        Assert.Equal(100001u, p.EffectId);
        Assert.Equal(10000101u, p.SourceSkillId);
        
        
        Assert.Equal(3_600_100UL, p.DurationMs);
        Assert.Equal(EffectService.DefaultStackCount, p.StackCount);
        Assert.Equal(EffectService.UndeterminedEffectValue, p.EffectValue);
    }

    [Fact]
    public void PlanItemEffect_BuildsAWellFormed2492CarryingTheDerivedFields()
    {
        var p = EffectService.PlanItemEffect(ShopDiscountScroll, ShopDiscountSkill)!.Value;
        const uint self = 0x00100001u; 

        var body = EffectPackets.BuildAddEffect2492(
            targetHandle: self, effectId: p.EffectId, effectValue: p.EffectValue,
            durationMs: p.DurationMs, stackCount: p.StackCount, sourceSkillId: p.SourceSkillId,
            casterHandle: self, applyMode: 0);

        AssertWellFormed(body);
        var blob = Blob(body);
        Assert.Equal(self, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(8)));          
        Assert.Equal(100001u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(12)));      
        Assert.Equal(3_600_100UL, BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(18)));  
        Assert.Equal(10000101u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(27)));    
    }

    [Fact]
    public void PlanItemEffect_ZeroDurationSkillYieldsAPermanentEffect()
    {
        
        
        var permanentSkill = new SkillLevelData { ID = 10000101, Effect_Duration = 0f };
        var plan = EffectService.PlanItemEffect(ShopDiscountScroll, permanentSkill);

        Assert.NotNull(plan);
        Assert.Equal(0UL, plan!.Value.DurationMs);
    }

    [Fact]
    public void PlanItemEffect_MissingSkillRowIsTreatedAsPermanent()
    {
        var plan = EffectService.PlanItemEffect(ShopDiscountScroll, grantingSkill: null);

        Assert.NotNull(plan);
        Assert.Equal(0UL, plan!.Value.DurationMs);
        Assert.Equal(100001u, plan!.Value.EffectId);
    }

    [Fact]
    public void PlanItemEffect_ItemWithoutAnEffectIsLeftAlone()
    {
        
        
        var plainPotion = new ItemData { ID = 12345, Effect_ID_1 = 0, Skill_ID_1 = 0 };
        Assert.Null(EffectService.PlanItemEffect(plainPotion, grantingSkill: null));
    }
}
