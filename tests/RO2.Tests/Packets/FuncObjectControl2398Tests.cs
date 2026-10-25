using System.Diagnostics;
using RO2.GameData;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// The 2398 OnChangeState pair of a function-object control (FuncObjectControlPackets) and the per-player
/// control store that sends the reset once per control (FuncObjectControlState).
/// </summary>
/// <remarks>
/// Expected bodies are reference frames, with the PC and object handles of each frame. The state byte
/// follows the object's FuncObject_Type.
/// </remarks>
public class FuncObjectControl2398Tests
{
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    [Theory]
    
    [InlineData((byte)1, 0x031AD211u, "011b1b00eae211d21a0311d21a030d0000000000000200000000000000")]
    
    [InlineData((byte)2, 0x031C2011u, "011b1b00eae211201c0311201c030e0000000000000200000000000000")]
    
    [InlineData((byte)4, 0x03947C11u, "011b1b00eae2117c9403117c9403100000000000000200000000000000")]
    
    [InlineData((byte)5, 0x03B66011u, "011b1b00eae21160b6031160b603110000000000000200000000000000")]
    public void ControlStart_IsRetailsFrame_ForEachFuncObjectType(byte funcObjectType, uint pc, string retail)
    {
        var state = FuncObjectControlPackets.ControlStateFor(funcObjectType);
        Assert.NotNull(state);
        Assert.Equal(retail, Hex(FuncObjectControlPackets.BuildControlState2398(pc, state.Value)));
    }

    [Theory]
    
    
    [InlineData((byte)8)]
    [InlineData((byte)0)]
    [InlineData((byte)6)]
    [InlineData((byte)9)]
    [InlineData((byte)12)]
    public void TypesWithNoRetailPose_SendNoStart(byte funcObjectType)
        => Assert.Null(FuncObjectControlPackets.ControlStateFor(funcObjectType));

    [Fact]
    public void NpcTarget_Gets0x14_WithoutATemplateLookup()
    {
        
        Assert.Equal((byte)0x14, FuncObjectControlPackets.ControlStateFor(new GameDataStore(), 1100, 0x028B4013u));
    }

    [Fact]
    public void HandleThisServerDidNotSpawn_GetsNoStart()
        => Assert.Null(FuncObjectControlPackets.ControlStateFor(new GameDataStore(), 0, 0x00157914u));

    [Theory]
    
    [InlineData(0x031C2011u, 0x001E7A14u, "011b1b00eae211201c0311201c030000000000000002000000147a1e00")]
    
    [InlineData(0x09D0C611u, 0x0008B714u, "011b1b00eae211c6d00911c6d009000000000000000200000014b70800")]
    public void ControlReset_IsRetailsFrame(uint pc, uint target, string retail)
        => Assert.Equal(retail, Hex(FuncObjectControlPackets.BuildControlReset2398(pc, target)));

    [Fact]
    public void CancelPair_AfterTheReset_IsRetailsFrames()
    {
        
        Assert.Equal("01101000f5e300000000147a1e0000000000",
            Hex(NpcInteractCancelHandler.BuildFuncObjectCancel2517(0x001E7A14u, 0)));
        Assert.Equal("01101000f6e30000000011201c03147a1e00",
            Hex(NpcInteractCancelHandler.BuildFuncObjectCancelNotify2518(0x031C2011u, 0x001E7A14u)));
    }

    [Fact]
    public void FirstCancelEndsTheControl_TheClientsCopyFindsNone()
    {
        
        var player = new PlayerState { EntityId = 0x031C2011u };
        FuncObjectControlState.Begin(player, new FuncObjectControl(0x001E7A14u, 0x0E));

        Assert.True(FuncObjectControlState.TryEnd(player, 0x001E7A14u, out var first));
        Assert.Equal((byte)0x0E, first.State);
        Assert.False(FuncObjectControlState.TryEnd(player, 0x001E7A14u, out _));
        Assert.Null(FuncObjectControlState.Current(player));
    }

    [Fact]
    public void CancelOfAnotherTarget_LeavesTheRunningControl()
    {
        var player = new PlayerState { EntityId = 0x031C2011u };
        FuncObjectControlState.Begin(player, new FuncObjectControl(0x001E7A14u, 0x0E));

        Assert.False(FuncObjectControlState.TryEnd(player, 0x0008B714u, out _));
        Assert.Equal(new FuncObjectControl(0x001E7A14u, 0x0E), FuncObjectControlState.Current(player));
    }

    
    
    
    private static readonly TimeSpan Casting80012130 = TimeSpan.FromMilliseconds(3000);

    [Fact]
    public void GatherEnd_OnceCastingHasRun_EndsTheControlOnce()
    {
        var player = new PlayerState { EntityId = 0x09D0C611u };
        FuncObjectControlState.Begin(player, new FuncObjectControl(0x0008B714u, 0x0D));
        var at1412 = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 3056 / 1000;

        Assert.Equal(FuncObjectControlEnd.Ended, FuncObjectControlState.TryEndAfter(player, 0x0008B714u,
            Casting80012130, at1412, out var control, out var elapsed));
        Assert.Equal(new FuncObjectControl(0x0008B714u, 0x0D), control);
        Assert.True(elapsed >= Casting80012130);
        Assert.Null(FuncObjectControlState.Current(player));

        
        Assert.Equal(FuncObjectControlEnd.NotRunning, FuncObjectControlState.TryEndAfter(player, 0x0008B714u,
            Casting80012130, at1412, out _, out _));
    }

    [Fact]
    public void GatherEnd_BeforeCasting_LeavesTheControlRunning()
    {
        var player = new PlayerState { EntityId = 0x09D0C611u };
        var beforeBegin = Stopwatch.GetTimestamp();
        FuncObjectControlState.Begin(player, new FuncObjectControl(0x0008B714u, 0x0D));

        Assert.Equal(FuncObjectControlEnd.TooEarly, FuncObjectControlState.TryEndAfter(player, 0x0008B714u,
            Casting80012130, beforeBegin, out var control, out _));
        Assert.Equal(default(FuncObjectControl), control);
        Assert.Equal(new FuncObjectControl(0x0008B714u, 0x0D), FuncObjectControlState.Current(player));

        
        Assert.True(FuncObjectControlState.TryEnd(player, 0x0008B714u, out var cancelled));
        Assert.Equal((byte)0x0D, cancelled.State);
    }

    [Fact]
    public void GatherEnd_WithNo1410_OrOnAnotherTarget_EndsNothing()
    {
        var player = new PlayerState { EntityId = 0x09D0C611u };
        var later = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10;
        Assert.Equal(FuncObjectControlEnd.NotRunning, FuncObjectControlState.TryEndAfter(player, 0x0008B714u,
            Casting80012130, later, out _, out var elapsed));
        Assert.Equal(TimeSpan.Zero, elapsed);

        FuncObjectControlState.Begin(player, new FuncObjectControl(0x001E7A14u, 0x0E));
        later = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10;
        Assert.Equal(FuncObjectControlEnd.NotRunning, FuncObjectControlState.TryEndAfter(player, 0x0008B714u,
            Casting80012130, later, out _, out _));
        Assert.Equal(new FuncObjectControl(0x001E7A14u, 0x0E), FuncObjectControlState.Current(player));
    }

    [Fact]
    public void NextStart_ReplacesTheControl_AndAPoselessStartIsKeptAsSuch()
    {
        var player = new PlayerState { EntityId = 0x031C2011u };
        FuncObjectControlState.Begin(player, new FuncObjectControl(0x001E7A14u, 0x0E));
        FuncObjectControlState.Begin(player, new FuncObjectControl(0x00108D14u, null));   

        Assert.False(FuncObjectControlState.TryEnd(player, 0x001E7A14u, out _));
        Assert.True(FuncObjectControlState.TryEnd(player, 0x00108D14u, out var control));
        Assert.Null(control.State);
    }
}
