using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Worlds.Application;
using RO2.Worlds.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1306 ReqActorViewData, a request for an actor the client cannot instantiate.
/// </summary>
/// <remarks>
/// The request body is 14 bytes: <c>[01 0c][u16 inner=12][u16 msgTag=58006][u32 0][u32 actorHandle]</c>.
/// The actor handle is a u32 at body offset 10.
/// The reply is 2319 OnNPCViewData (see <see cref="NpcSpawnService.BuildOnNpcViewData2319"/>).
/// No reply is sent for player (PC) targets.
/// </remarks>
public class ActorViewDataHandler : ZoneRmiHandlerBase<ActorViewDataHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqActorViewData;
    public override string Name => "ActorViewData";

 /// <summary>Offset of the u32 actor handle within the 1306 body.</summary>
    internal const int HandleOffset = 10;

 /// <summary>Expected length of the 1306 body in bytes.</summary>
    internal const int BodyLength = 14;

 /// <summary>Width in bytes of the actor handle (a u32).</summary>
    private const int HandleSize = 4;

 /// <summary>Mask of the low byte of an actor handle, which tags the actor kind (0x13 NPC, 0x11 PC).</summary>
    private const uint ActorKindTagMask = 0xFF;

    public ActorViewDataHandler(IServiceScopeFactory s, ILogger<ActorViewDataHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var body = payload.Span;
        if (body.Length < HandleOffset + HandleSize)
        {
            Logger.LogDebug("Session {Id}: ActorViewData body is {Len} bytes, need at least {Need}",
                session.SessionId, body.Length, HandleOffset + HandleSize);
            return;
        }

        
        
        
        
        
        if (!CompactEnvelope.IsWidthMarker(body[0]))
        {
            Logger.LogWarning("Session {Id}: ActorViewData envelope marker is 0x{Marker:X2}, not in {{1,2,4,8}}",
                session.SessionId, body[0]);
            return;
        }

        var targetEntityId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            body.Slice(HandleOffset, HandleSize));

        var npcState = services.GetRequiredService<NpcStateManager>();
        var npc = npcState.GetNpc(targetEntityId);
        var reply = SpawnVisibilityUseCase.DecideViewData(npc is not null,
            () => services.GetRequiredService<GameDataStore>().Npcs.ContainsKey(npc!.NpcId));
        if (reply == ViewDataReply.UnknownActor || npc is null)   // npc is null is implied by the reply; it only lets the compiler see it
        {
            
            
            Logger.LogDebug("Session {Id}: ActorViewData for {Target:X8} (tag 0x{Tag:X2}) is not a known NPC",
                session.SessionId, targetEntityId, targetEntityId & ActorKindTagMask);
            return;
        }

        
        
        
        
        
        
        var data = services.GetRequiredService<GameDataStore>();
        if (reply == ViewDataReply.NoClientTemplate)
        {
            Logger.LogWarning(
                "Session {Id}: ActorViewData for {Target:X8} asset {Asset} has no NPCInfo row; not replying. "
                + "This actor should not have been announced or moved -- see NpcSpawnService.HasClientTemplate",
                session.SessionId, targetEntityId, npc.NpcId);
            return;
        }

        
        
        
        
        
        
        var walkSpeed = data.GetNpcStats(npc.NpcId)?.Walk_Speed ?? 0f;

        
        
        
        var effects = npc.IsDead
            ? null
            : services.GetRequiredService<EffectService>().NpcEffectsSnapshot(npc.EntityOid);

        
        
        
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnNPCViewData,
            NpcSpawnService.BuildOnNpcViewData2319(npc, walkSpeed, DateTime.UtcNow, effects), ct);
    }
}
