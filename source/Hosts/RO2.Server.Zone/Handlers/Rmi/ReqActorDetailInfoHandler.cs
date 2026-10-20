using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1307 (ReqActorDetailInfo). Replies with a 2643 AnsActorIconInfo panel for the selected
/// actor, and owns the per-player selection table that drives unsolicited 2643 pushes.
/// </summary>
/// <remarks>
/// Request layout: body size 14, compact-width marker 0x01, selfLen 12, msgTag 58062. The requester's
/// handle is at body+6 and the selected actor's handle at body+10.
/// <para>
/// A PC or NPC selection is answered with a 2643 carrying the same handle. A zero target (deselect) and
/// a self-selection get no answer. A PC that is not in view of the requester gets no answer either, but
/// the selection is still recorded and watchers are still updated.
/// </para>
/// <para>
/// The server also pushes 2643 to players who have selected an actor whenever that actor's own target
/// changes. See <see cref="PushIconToWatchersAsync"/>.
/// </para>
/// </remarks>
public class ReqActorDetailInfoHandler : ZoneRmiHandlerBase<ReqActorDetailInfoHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqActorDetailInfo; 
    public override string Name => "ReqActorDetailInfo";

 /// <summary>Offset of the u32 target actor handle in the 1307 body.</summary>
    private const int TargetHandleOffset = 10;

 /// <summary>Width in bits of the actor type tag in the low byte of a handle; the session or spawn counter sits above it.</summary>
    private const int HandleTagBits = 8;

 /// <summary>Mask that isolates the actor type tag (low byte) of a handle.</summary>
    private const uint HandleTagMask = 0xFF;

    private const byte PcTag = PlayerStateManager.PcActorTag; 
    private const byte NpcTag = NpcStateManager.NpcActorTag;  

 /// <summary>
 /// Last actor selected by each player (0 after a deselect). Used for the target-of-target field of 2643
 /// panels and to address unsolicited pushes. Keyed weakly on the player state, so entries are released
 /// with the player's zone session.
 /// </summary>
    private static readonly ConditionalWeakTable<PlayerState, StrongBox<uint>> Selection = new();

 /// <summary>The actor <paramref name="player"/> currently has selected, or 0.</summary>
    internal static uint SelectedTargetOf(PlayerState player)
        => Selection.TryGetValue(player, out var box) ? box.Value : 0u;

    public ReqActorDetailInfoHandler(IServiceScopeFactory s, ILogger<ReqActorDetailInfoHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        uint targetHandle;
        {
            var body = payload.Span;
            if (body.Length < TargetHandleOffset + sizeof(uint)) return;
            
            
            
            if (!CompactEnvelope.IsWidthMarker(body[0])) return;
            targetHandle = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                body.Slice(TargetHandleOffset, sizeof(uint)));
        }

        Selection.GetValue(player, _ => new StrongBox<uint>()).Value = targetHandle;

        
        
        await PushIconToWatchersAsync(Broadcast, PlayerManager, player.MapId, player.EntityId,
            BuildIconOf(player), ct);

        if (targetHandle == 0) return;                
        if (targetHandle == player.EntityId) return;  

        byte[] icon;
        switch ((byte)(targetHandle & HandleTagMask))
        {
            case PcTag:
            {
                var target = PlayerManager.GetPlayer(targetHandle >> HandleTagBits);
                if (target is null || target.EntityId != targetHandle)
                {
                    Logger.LogDebug("Session {Id}: ReqActorDetailInfo target {Target:X8} has no live PC", session.SessionId, targetHandle);
                    return;
                }
                if (!IsInViewOf(player, target))
                {
                    Logger.LogDebug("Session {Id}: ReqActorDetailInfo target {Target:X8} is not in view (map {TargetMap} vs {Map}, loaded {Loaded}); not answered",
                        session.SessionId, targetHandle, target.MapId, player.MapId, target.MapLoaded && !target.PendingGliderDismount);
                    return;
                }
                icon = BuildIconOf(target);
                break;
            }
            case NpcTag:
            {
                var npc = services.GetRequiredService<NpcStateManager>().GetNpc(targetHandle);
                if (npc is null)
                {
                    Logger.LogDebug("Session {Id}: ReqActorDetailInfo target {Target:X8} is not a known NPC", session.SessionId, targetHandle);
                    return;
                }
                icon = BuildIconOf(npc);
                break;
            }
            default:
                return; 
        }

        await session.SendRmiAsync(ZoneS2COpcodes.AnsActorIconInfo, icon, ct);
    }

 /// <summary>
 /// Whether a 1307 from <paramref name="requester"/> naming the PC <paramref name="target"/> gets a 2643.
 /// </summary>
 /// <remarks>
 /// The target must be on the requester's map and have finished loading it. PCs on other maps, PCs that
 /// have left, and PCs still loading are not in view. NPC targets are not gated by view.
 /// </remarks>
    internal static bool IsInViewOf(PlayerState requester, PlayerState target)
        => target.MapId == requester.MapId && target.MapLoaded && !target.PendingGliderDismount;

 /// <summary>Builds the 2643 panel of a PC from its live vitals, level and current selection.</summary>
 /// <remarks>A self-selection is reported as no target.</remarks>
    internal static byte[] BuildIconOf(PlayerState target)
    {
        var selected = SelectedTargetOf(target);
        return CombatPackets.BuildActorIconInfo2643(
            target.EntityId,
            (uint)Math.Max(0, target.Hp), (uint)Math.Max(0, target.MaxHp),
            (ushort)Math.Clamp(target.Mp, 0, ushort.MaxValue),
            (ushort)Math.Clamp(target.MaxMp, 0, ushort.MaxValue),
            target.Level,
            targetOfTarget: selected == target.EntityId ? 0u : selected);
    }

 /// <summary>
 /// Builds the 2643 panel of an NPC from its live HP, level, advertised SP and current target.
 /// </summary>
 /// <remarks>
 /// The target-of-target field is the NPC's current aggro target, or 0 when its threat table is empty or
 /// the NPC is dead.
 /// </remarks>
    internal static byte[] BuildIconOf(NpcRuntimeState npc)
    {
        var sp = (ushort)Math.Min(SkillDamagePackets.NpcTargetSp(npc), ushort.MaxValue);
        return CombatPackets.BuildActorIconInfo2643(
            npc.EntityOid,
            (uint)Math.Max(0, npc.Hp), (uint)Math.Max(0, npc.MaxHp),
            sp, sp,
            (uint)Math.Max(0, npc.Level),
            targetOfTarget: npc.AggroTargetHandle);
    }

 /// <summary>
 /// Sends <paramref name="icon"/> to every player on <paramref name="mapId"/> whose current selection is
 /// <paramref name="actorHandle"/>, except the actor itself.
 /// </summary>
 /// <remarks>
 /// Used for unsolicited 2643 pushes, which are triggered by a change in the actor's own target.
 /// </remarks>
    internal static async Task PushIconToWatchersAsync(BroadcastService broadcast, PlayerStateManager players,
        uint mapId, uint actorHandle, byte[] icon, CancellationToken ct = default)
    {
        if (actorHandle == 0)
            return;
        foreach (var watcher in players.GetPlayersOnMap(mapId).ToArray())
        {
            if (watcher.EntityId == actorHandle || SelectedTargetOf(watcher) != actorHandle)
                continue;
            var session = broadcast.GetSession(watcher.SessionId);
            if (session is null || !session.IsConnected)
                continue;
            await session.SendRmiAsync(ZoneS2COpcodes.AnsActorIconInfo, icon, ct);
        }
    }
}
