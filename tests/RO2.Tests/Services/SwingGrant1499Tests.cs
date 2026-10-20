using System.Buffers.Binary;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Services;

/// <summary>
/// One damaging impact per granted swing (AutoAttackState.TryConsumeSwing), and the two 2695 codes that accompany it:
/// 20158 for a 1499 with no swing pending, and 20014 for a 1498 on a handle that is not an actor.
/// </summary>
/// <remarks>
/// The first 1499 after a 1498 is granted a swing in nearly every case, and a repeat 1499 gets a 20158 in most cases.
/// The frames below are reference frames.
/// </remarks>
public class SwingGrant1499Tests
{
    [Fact]
    public void GrantedSwing_TakesTheFirstImpactOnly()
    {
        
        
        var player = new PlayerState { SessionId = 0xA51001, EntityId = PlayerStateManager.BuildActorHandle(0xA51001) };
        var impact = Convert.FromHexString("0100000013983202");
        var target = BinaryPrimitives.ReadUInt32LittleEndian(impact.AsSpan(4));
        Assert.Equal(0x13, (byte)target);   

        AutoAttackState.Engage(player, target);
        Assert.True(AutoAttackState.TryConsumeSwing(player));
        Assert.False(AutoAttackState.TryConsumeSwing(player));

        Assert.Equal(Convert.FromHexString("be4e000000000000000000000000"),
            CombatPackets.BuildAttackRefused2695(NormalAttackDamageHandler.ImpactRefusedNoSwing));
    }

    [Fact]
    public void NoGrant_NoSwing()
    {
        var player = new PlayerState { SessionId = 0xA51002, EntityId = PlayerStateManager.BuildActorHandle(0xA51002) };
        Assert.False(AutoAttackState.TryConsumeSwing(player));
    }

    [Fact]
    public void EachGrantArmsOneImpact()
    {
        var player = new PlayerState { SessionId = 0xA51003, EntityId = PlayerStateManager.BuildActorHandle(0xA51003) };
        AutoAttackState.Engage(player, 0x0000D413);
        AutoAttackState.Engage(player, 0x0000D413);   
        Assert.True(AutoAttackState.TryConsumeSwing(player));
        Assert.False(AutoAttackState.TryConsumeSwing(player));
        AutoAttackState.Engage(player, 0x0000D413);   
        Assert.True(AutoAttackState.TryConsumeSwing(player));
    }

    [Fact]
    public void Disengage_DoesNotSpendThePendingImpact()
    {
        
        
        var player = new PlayerState { SessionId = 0xA51004, EntityId = PlayerStateManager.BuildActorHandle(0xA51004) };
        AutoAttackState.Engage(player, 0x0000D413);
        AutoAttackState.Disengage(player);
        Assert.True(AutoAttackState.TryConsumeSwing(player));
    }

    [Fact]
    public void NotAnActor_IsRefusedWith20014()
    {
        
        
        var request = Convert.FromHexString("c400005034d38db3");
        var target = BinaryPrimitives.ReadUInt32LittleEndian(request);
        Assert.Equal(0x500000C4u, target);
        Assert.False(AutoAttackState.IsActorHandle(target));
        Assert.Equal(Convert.FromHexString("2e4e000000000000000000000000"),
            CombatPackets.BuildAttackRefused2695(NormalAttackStartHandler.AttackRefusedNotAnActor));
    }

    [Theory]
    [InlineData(0x0000D413u)]   
    [InlineData(0x06EF4D11u)]   
    [InlineData(0x00000C14u)]   
    public void ActorTags_AreAttackable(uint handle)
        => Assert.True(AutoAttackState.IsActorHandle(handle));
}
