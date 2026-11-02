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

    /// <summary>
    /// The host side of <see cref="VictimDropPass"/> for one dying player: works on the monsters of the snapshot the
    /// caller took, so the removal and its 2630 hit the same instances the released list maps back to.
    /// </summary>
    private sealed class VictimThreatPorts(NpcCombatService owner, PlayerState victim,
        IReadOnlyDictionary<uint, NpcRuntimeState> monsters) : IVictimThreatPorts
    {
        public VictimRemoval RemoveVictim(uint monsterOid, uint victimHandle)
        {
            var npc = monsters[monsterOid];
            lock (npc.ThreatLock)
            {
                bool held = npc.ThreatTable.Remove(victimHandle);
                // Only losing the TARGET re-selects. A monster keeps its current target while that row
                // is on the table (it moves only when another row passes it by more than 10%). So removing someone else must not
                // move the target. (NpcStateManager.AddThreatCore does not apply that 10% margin: it
                // makes the top row the target on every add, so the target is the top row and removing
                // another row leaves it the top row anyway.)
                if (held && npc.AggroTargetHandle == victimHandle)
                    npc.ThreatTable.ReselectTop();
                return new VictimRemoval(held, npc.AggroTargetHandle);
            }
        }

        public async Task NotifyVictimRemovedAsync(uint monsterOid, VictimRemoval removal)
        {
            var npc = monsters[monsterOid];
            // To the players whose open meter is this monster (SendAggroChangeAsync), as every 2630.
            await owner.SendAggroChangeAsync(npc.EntityOid, npc.MapId, AggroPackets.BuildAggroChanged2630(
                npc.EntityOid, removal.Target, victim.EntityId, victim.Name, 0, changeFlag: AggroEntryRemoved));
            // The re-selected target reaches whoever has this monster selected, as any other
            // target change does.
            await owner.PushTargetChangesAsync(new[] { npc.EntityOid }, CancellationToken.None);
        }
    }
}
