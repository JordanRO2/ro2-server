using RO2.Monster.Application;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

public partial class NpcCombatService
{
    /// <summary>The player facts the retaliation pass reads, or null for no player.</summary>
    private static PlayerFacts? FactsOf(PlayerState? p)
        => p is null ? null : new PlayerFacts(p.EntityId, p.Name, p.IsDead, p.MapId);

    /// <summary>
    /// The single host adapter of the Monster.Application ports over the live NpcStateManager, the player registry and
    /// the wire senders. A nested class so the target resolution and the target re-selection stay the service's own.
    /// </summary>
    private sealed class MonsterPorts(NpcCombatService owner)
        : IMonsterReadView, IMonsterCommands, IMonsterLifecycle, IRetaliationNotifier
    {
        public PlayerFacts? FindPlayer(uint pcHandle) => FactsOf(owner.ResolvePlayer(pcHandle));

        public uint AggroTargetOf(uint monsterOid) => owner._npcState.GetNpc(monsterOid)?.AggroTargetHandle ?? 0;

        public bool AddThreat(uint monsterOid, uint pcHandle, string? pcName, long amount)
            => owner._npcState.AddThreat(monsterOid, pcHandle, pcName, amount);

        public bool LeashReset(uint monsterOid) => owner._npcState.LeashReset(monsterOid);

        public uint ReselectLiveTarget(uint monsterOid)
            => owner._npcState.GetNpc(monsterOid) is { } npc ? owner.ReselectLiveTarget(npc) : 0;

        public uint Respawn(uint monsterOid) => owner._npcState.Respawn(monsterOid);

        public IReadOnlyList<uint> GetRespawnReady()
            => owner._npcState.GetRespawnReady().Select(n => n.EntityOid).ToArray();

        public void RetireCorpse(uint corpseHandle, DateTime? deathStamp)
            => owner._npcState.RetireCorpse(corpseHandle, deathStamp);

        public async Task EnterCombatAsync(uint monsterOid, uint pcHandle)
        {
            if (owner._npcState.GetNpc(monsterOid) is { } npc)
                await owner._npcChat.EnterCombatAsync(npc, pcHandle);
        }

        public async Task NotifyLeashHealedAsync(uint monsterOid)
        {
            if (owner._npcState.GetNpc(monsterOid) is { } npc)
                await ActorVitalsBroadcast.SendNpcAutoHealAsync(owner._broadcast, npc);
        }
    }
}
