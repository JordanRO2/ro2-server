using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// The looting state around a corpse loot window: the 2398 0x0C that follows the 2374 list answering a 1338
/// (NpcShopOpenHandler), and the 2398 state 0 that answers the 1339 close (NpcShopCloseHandler), gated on that state.
/// </summary>
/// <remarks>
/// One exchange: a C2S 1338 is answered by 2374 and 2398 0x0C; a later C2S 1339 on the same corpse is answered
/// by 2398 state 0.
/// </remarks>
public class CorpseLootWindow1338Tests
{
    private const uint Looter = 0x0B495E11u;
    private const uint Corpse = 0x28601713u;

    [Fact]
    public void LootingState2398_IsTheFrameRetailSendsAfterThe2374()
    {
        Assert.Equal(Convert.FromHexString("011b1b00eae2115e490b115e490b0c0000000000000200000013176028"),
            NpcShopOpenHandler.BuildLootingState2398(Looter, Corpse));
    }

    [Fact]
    public void LootEndState2398_IsTheFrameRetailAnswers1339With()
    {
        Assert.Equal(Convert.FromHexString("011b1b00eae2115e490b115e490b000000000000000200000000000000"),
            NpcShopCloseHandler.BuildLootEndState2398(Looter));
    }

    [Fact]
    public void LootingState_EndsOncePerOpen()
    {
        var player = new PlayerState { EntityId = Looter };

        Assert.False(NpcShopOpenHandler.TryEndLooting(player, out _)); 

        NpcShopOpenHandler.MarkLooting(player, 0x28600213u);
        NpcShopOpenHandler.MarkLooting(player, Corpse);                  
        Assert.True(NpcShopOpenHandler.TryEndLooting(player, out var corpse));
        Assert.Equal(Corpse, corpse);

        Assert.False(NpcShopOpenHandler.TryEndLooting(player, out _));  
    }

    [Fact]
    public void LootingState_IsPerPlayer()
    {
        var a = new PlayerState { EntityId = Looter };
        var b = new PlayerState { EntityId = 0x0B494611u };

        NpcShopOpenHandler.MarkLooting(a, Corpse);
        Assert.False(NpcShopOpenHandler.TryEndLooting(b, out _));
        Assert.True(NpcShopOpenHandler.TryEndLooting(a, out _));
    }
}
