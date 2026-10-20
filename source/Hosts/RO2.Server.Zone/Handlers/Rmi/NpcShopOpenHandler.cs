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
/// Handles the open of a loot window (RMI 1338, ReqItemListInNPC) for an NPC actor, which is a corpse's loot list.
/// </summary>
/// <remarks>
/// <para>
/// A corpse with loot for the requester is answered with the item list (2374) and the looting state
/// (2398, state 0x0C) to the looter and to the other players on the map. The player is then recorded as looting.
/// </para>
/// <para>
/// A corpse with nothing left for the requester gets a revoke (2471 with granted = 0) instead, and no loot window opens.
/// A live NPC gets no reply.
/// </para>
/// <para>
/// Body: <c>[01 0c][u16 innerSize=12][u16 msgTag=0xE3DF][u32 own PC handle][u32 target NPC handle]</c>.
/// The target is at body+10.
/// </para>
/// </remarks>
public class NpcShopOpenHandler : ZoneRmiHandlerBase<NpcShopOpenHandler>
{
 /// <summary>The msgTag of the 1338 request (0xE3DF, 58335), at body+4.</summary>
    internal const ushort MsgTagReqItemListInNPC = 0xE3DF;

 /// <summary>The size of the 1338 request body: 2-byte header, u16 innerSize, u16 msgTag, own PC handle, target NPC handle.</summary>
    private const int RequestBodyLength = 14;

 /// <summary>The offset of the target NPC handle in the 1338 request body (after the envelope and the own PC handle).</summary>
    private const int TargetHandleOffset = 10;

 /// <summary>The 2398 state value of a player whose loot window is open (0x0C).</summary>
    internal const byte LootingState = 0x0C;

 /// <summary>The trailer value (blob+19) of the looting-state 2398.</summary>
    internal const uint LootingStateTrailer = 2;

 /// <summary>The corpse whose window each player has open, set by a 1338 answered with a list.</summary>
    private static readonly ConditionalWeakTable<PlayerState, StrongBox<uint>> OpenLootWindows = new();

    public override ushort Opcode => ZoneC2SOpcodes.ReqItemListInNPC;
    public override string Name => "NpcShopOpen";

    public NpcShopOpenHandler(IServiceScopeFactory s, ILogger<NpcShopOpenHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < RequestBodyLength) return;
        var reader = new PacketReader(payload.Span);
        reader.Skip(TargetHandleOffset); // [01 0c][u16 innerSize][u16 msgTag][u32 own PC handle]
        var npcHandle = reader.ReadUInt32();

        var npc = services.GetRequiredService<NpcStateManager>().GetNpc(npcHandle);
        if (npc is not { IsDead: true })
        {
            Logger.LogDebug("Session {Id}: {Name} requested the item list of NPC {Handle:X8} (npc={NpcId}, alive); nothing to list, no reply",
                session.SessionId, player.Name, npcHandle, npc?.NpcId ?? 0);
            return;
        }

        
        if (GroundItemService.TryBuildCorpseList2374(player.MapId, npcHandle, player.CharacterId, npc.DeathTime, out var list))
        {
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnHeartBeatItem, list);

            var looting = BuildLootingState2398(player.EntityId, npcHandle);
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnChangeState, looting);
            await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.OnChangeState, w => w.WriteBytes(looting));

            MarkLooting(player, npcHandle);
            Logger.LogDebug("Session {Id}: {Name} opened corpse {Handle:X8}: 2374 list + 2398 state 0x0C",
                session.SessionId, player.Name, npcHandle);
            return;
        }

        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnLootingAuthority,
            CombatPackets.BuildLootingAuthority2471(npcHandle, granted: false));
        Logger.LogDebug("Session {Id}: {Name} opened corpse {Handle:X8}, which holds nothing for them "
            + "(emptied, never loaded, or another's): loot authority revoked (2471 granted=0)",
            session.SessionId, player.Name, npcHandle);
    }

 /// <summary>
 /// Builds the 2398 OnChangeState that puts <paramref name="looterHandle"/> in the looting state over <paramref name="corpseHandle"/>.
 /// </summary>
    internal static byte[] BuildLootingState2398(uint looterHandle, uint corpseHandle)
        => CombatPackets.BuildChangeState2398(looterHandle, LootingState, corpseHandle, LootingStateTrailer);

 /// <summary>Remembers that <paramref name="player"/>'s loot window is open on <paramref name="corpseHandle"/>.</summary>
    internal static void MarkLooting(PlayerState player, uint corpseHandle)
        => OpenLootWindows.AddOrUpdate(player, new StrongBox<uint>(corpseHandle));

 /// <summary>
 /// Ends the looting state of <paramref name="player"/>. Returns true, with the corpse that was opened, if the player was in that state.
 /// </summary>
 /// <remarks>
 /// The state is kept with the PlayerState, so a character that logs in again starts outside it.
 /// </remarks>
    internal static bool TryEndLooting(PlayerState player, out uint corpseHandle)
    {
        if (OpenLootWindows.TryGetValue(player, out var box) && OpenLootWindows.Remove(player))
        {
            corpseHandle = box.Value;
            return true;
        }
        corpseHandle = 0;
        return false;
    }
}
