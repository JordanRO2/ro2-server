using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles a request to open a monster's threat meter (RMI 1466, ReqOpenAggroList).
/// </summary>
/// <remarks>
/// <para>
/// Request body: <c>[u32 actorHandle][u8 open]</c>. Only monster handles (NPC tag 0x13) are answered.
/// A request that names a player is ignored.
/// </para>
/// <para>
/// The reply (2629 AnsAggroList) carries the monster's threat table: the current target first, then the other rows
/// by descending threat. A monster the server does not know gets no reply.
/// </para>
/// <para>
/// The empty reply is 10 bytes: <c>[u32 monster][u32 0][CompactLen 0]</c>. The wire shape is built by <see cref="AggroPackets"/>.
/// </para>
/// </remarks>
public class OpenAggroListHandler : ZoneRmiHandlerBase<OpenAggroListHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqOpenAggroList;
    public override string Name => "OpenAggroList";

 /// <summary>Length of the request body in bytes.</summary>
    private const int RequestBytes = 5;

    /// <summary>Mask of an actor handle's low byte, its actor type tag (0x11 PC, 0x13 NPC).</summary>
    private const uint ActorTagMask = 0xFF;

    /// <summary>Current-target handle of a 2629 whose table has no row for the target: 0 (none).</summary>
    private const uint NoCurrentTarget = 0;

    public OpenAggroListHandler(IServiceScopeFactory s, ILogger<OpenAggroListHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < RequestBytes) return;
        var reader = new PacketReader(payload.Span);
        var actorHandle = reader.ReadUInt32();   

        
        if ((actorHandle & ActorTagMask) != NpcStateManager.NpcActorTag)
        {
            Logger.LogDebug("1466 from {Name}: handle {Handle:X8} is not a monster; no answer sent",
                player.Name, actorHandle);
            return;
        }

        
        
        var npc = services.GetRequiredService<NpcStateManager>().GetNpc(actorHandle);
        if (npc is null)
        {
            Logger.LogDebug("1466 from {Name}: {Handle:X8} is not a known NPC; no answer sent",
                player.Name, actorHandle);
            return;
        }

        
        
        services.GetRequiredService<NpcCombatService>().WatchAggroMeter(player, actorHandle);

        var entries = SnapshotThreat(npc, out var currentTarget);
        var body = AggroPackets.BuildAggroList2629(actorHandle, currentTarget, entries);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsAggroList, writer => writer.WriteBytes(body), ct);
    }

 /// <summary>
 /// Returns the monster's live threat table in wire order: the current target first, then the other rows by descending threat.
 /// </summary>
 /// <remarks>
 /// The rows are the same ones that feed the 2630 threat deltas. Threat is one point per point of damage;
 /// skill threat is not modelled.
 /// </remarks>
    internal static List<AggroPackets.AggroEntry> SnapshotThreat(NpcRuntimeState npc, out uint currentTarget)
    {
        var entries = new List<AggroPackets.AggroEntry>();
        AggroPackets.AggroEntry? head = null;
        lock (npc.ThreatLock)
        {
            currentTarget = npc.AggroTargetHandle;
            foreach (var row in npc.Threat.Values)
            {
                
                var entry = new AggroPackets.AggroEntry(row.AttackerHandle, row.Name,
                    (uint)Math.Clamp(row.Threat, 0, uint.MaxValue));
                if (row.AttackerHandle == currentTarget)
                    head = entry;
                else
                    entries.Add(entry);
            }
        }
        entries.Sort((a, b) => b.Threat.CompareTo(a.Threat));
        if (head is { } h)
            entries.Insert(0, h);
        else
            currentTarget = NoCurrentTarget;
        return entries;
    }
}
