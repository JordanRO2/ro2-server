using RO2.Combats.Application;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Host adapter for <see cref="ICombatantView"/> over a live NPC lookup (NpcStateManager.GetNpc): resolves a
/// handle to the <see cref="Combatant"/> facts the cast-path flows read, or null when it is not a live NPC.
/// </summary>
internal sealed class NpcCombatantView(Func<uint, NpcRuntimeState?> npcOf) : ICombatantView
{
    public Combatant? Find(uint handle)
        => npcOf(handle) is { } npc ? new Combatant(npc.EntityOid, npc.IsDead, npc.Camp) : null;
}
