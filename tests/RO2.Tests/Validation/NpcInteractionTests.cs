using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Validation;

public class NpcInteractionTests
{
    private static NpcRuntimeState Npc(uint map = 1100) => new() { MapId = map, PosX = 100f, PosY = 100f };

    [Fact]
    public void GateInReach_IsHorizontalDistanceOnTheSameMap()
    {
        var npc = Npc();
        var r = NpcInteraction.DefaultInteractRadius;
        Assert.True(NpcInteraction.GateInReach(new PlayerState { MapId = 1100, X = 100f + r, Y = 100f }, npc, r));
        Assert.False(NpcInteraction.GateInReach(new PlayerState { MapId = 1100, X = 100f + r + 1f, Y = 100f }, npc, r));
        Assert.False(NpcInteraction.GateInReach(new PlayerState { MapId = 1101, X = 100f, Y = 100f }, npc, r)); // other map
    }

    [Fact]
    public void GateInReach_IsNaNSafe()
    {
        var npc = Npc();
        Assert.False(NpcInteraction.GateInReach(new PlayerState { MapId = 1100, X = float.NaN, Y = 100f }, npc, 40f));
        Assert.False(NpcInteraction.GateInReach(new PlayerState { MapId = 1100, X = 100f, Y = 100f }, npc, float.NaN));
        npc.PosY = float.NaN;
        Assert.False(NpcInteraction.GateInReach(new PlayerState { MapId = 1100, X = 100f, Y = 100f }, npc, 40f));
    }
}
