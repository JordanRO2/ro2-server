using RO2.Monster.Application;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Network.Protocol;

namespace RO2.Server.Zone.Services;

/// <summary>
/// The kill grant every damage path sends for a <see cref="MonsterDied"/>: the credit (quest ticks, Khara, EXP), then the
/// 2471 OnLootingAuthority unicast to the killer, then (where the path wants it adjacent) the corpse. Only this
/// common run is shared; each path keeps its own death panel, engagement close and disengage, in its own order.
/// </summary>
internal static class MonsterKillGrant
{
    /// <summary>Credit, then the 2471 grant (LootService.ProcessKillCreditAsync, then OnLootingAuthority).</summary>
    public static async Task SendCreditAndAuthorityAsync(
        LootService loot, ProudSession session, PlayerState killer, MonsterDied died)
    {
        await loot.ProcessKillCreditAsync(session, killer, died.Handle, died.NpcId);
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnLootingAuthority,
            CombatPackets.BuildLootingAuthority2471(died.Handle));
    }

    /// <summary>Credit, the 2471 grant, then the corpse (2392, drops, deferred 2507): retail's whole kill order.</summary>
    public static async Task SendAsync(LootService loot, ProudSession session, PlayerState killer, MonsterDied died)
    {
        await SendCreditAndAuthorityAsync(loot, session, killer, died);
        await loot.ProcessNpcCorpseAsync(session, killer, died.Handle, died.NpcId);
    }
}
